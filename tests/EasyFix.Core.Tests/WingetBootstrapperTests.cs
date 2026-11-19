using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests directos de <see cref="WingetBootstrapper"/> — la clase real, no el fake que usan los
/// tests de <c>WingetService</c>. Con dobles de descarga, proceso y localización, así que se
/// ejercita entera sin tocar Windows ni la red.
/// </summary>
public sealed class WingetBootstrapperTests
{
    private const string WingetPath = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.2\winget.exe";
    private static readonly Uri BundleUrl =
        new("https://github.com/microsoft/winget-cli/releases/download/v1/Microsoft.DesktopAppInstaller.msixbundle");

    /// <summary>Devuelve rutas a archivos temporales REALES, para poder verificar que se borran.</summary>
    private sealed class FakeDownloader : IFileDownloader
    {
        public List<string> DownloadedPaths { get; } = new();
        public Uri? LatestUrl { get; set; } = BundleUrl;
        public Exception? ResolveThrows { get; set; }
        public Exception? DownloadThrows { get; set; }

        /// <summary>
        /// Para el test de escaping: simula que la ruta descargada trae una comilla simple, algo
        /// que EasyFix nunca produciría con sus nombres fijos, pero que <c>PsQuote</c> tiene que
        /// poder manejar sin romper el script igual.
        /// </summary>
        public bool InjectSingleQuoteInPath { get; set; }

        public Task<string> DownloadAsync(Uri url, string fileName, IProgress<string>? progress, CancellationToken ct)
        {
            if (DownloadThrows is { } ex)
            {
                throw ex;
            }

            string marker = InjectSingleQuoteInPath ? "o'brien-" : string.Empty;
            string path = Path.Combine(Path.GetTempPath(), $"wbt-{marker}{Guid.NewGuid()}-{fileName}");
            File.WriteAllText(path, "contenido de prueba");
            DownloadedPaths.Add(path);
            return Task.FromResult(path);
        }

        public Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct)
        {
            if (ResolveThrows is { } ex)
            {
                throw ex;
            }

            Assert.Equal("microsoft/winget-cli", repository);
            return Task.FromResult(LatestUrl);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Func<ProcessResult>? _respond;
        private readonly Exception? _throws;

        public FakeRunner(Func<ProcessResult>? respond = null, Exception? throws = null)
        {
            _respond = respond;
            _throws = throws;
        }

        public string? LastExecutable { get; private set; }
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<ProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken ct = default, IReadOnlyCollection<int>? benignExitCodes = null)
        {
            LastExecutable = executablePath;
            LastArguments = arguments;

            if (_throws is { } ex)
            {
                throw ex;
            }

            return Task.FromResult(_respond?.Invoke() ?? new ProcessResult(0, "", "", false, TimeSpan.Zero));
        }
    }

    /// <summary>Simula lo que devolvería el locator antes y después de instalar.</summary>
    private sealed class SequencedLocator : IWingetLocator
    {
        private readonly string?[] _sequence;
        private int _call;
        public SequencedLocator(params string?[] sequence) => _sequence = sequence;
        public string? Find() => _sequence[Math.Min(_call++, _sequence.Length - 1)];
    }

    private static WingetBootstrapper Build(FakeDownloader downloader, FakeRunner runner, IWingetLocator locator) =>
        new(downloader, runner, locator, NullLogger<WingetBootstrapper>.Instance);

    // ---- Caso feliz ---------------------------------------------------------------------------

    [Fact]
    public async Task WingetYaDisponible_NoDescargaNiEjecutaNada()
    {
        var downloader = new FakeDownloader();
        var runner = new FakeRunner();

        WingetBootstrapResult result = await Build(downloader, runner, new SequencedLocator(WingetPath))
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(downloader.DownloadedPaths);
        Assert.Null(runner.LastExecutable);
    }

    [Fact]
    public async Task Falta_DescargaInstalaYQuedaDisponible_YBorraLosArchivosDescargados()
    {
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(() => new ProcessResult(0, "", "", false, TimeSpan.Zero));
        var locator = new SequencedLocator(null, WingetPath); // falta, después de instalar aparece

        WingetBootstrapResult result = await Build(downloader, runner, locator)
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, downloader.DownloadedPaths.Count); // bundle + VCLibs

        foreach (string path in downloader.DownloadedPaths)
        {
            Assert.False(File.Exists(path)); // limpiado en el finally
        }
    }

    [Fact]
    public async Task InvocaPowershellConRutaAbsolutaYSinCmd()
    {
        var downloader = new FakeDownloader();
        var runner = new FakeRunner();
        var locator = new SequencedLocator(null, WingetPath);

        await Build(downloader, runner, locator).EnsureAvailableAsync(null, CancellationToken.None);

        Assert.NotNull(runner.LastExecutable);
        Assert.True(Path.IsPathRooted(runner.LastExecutable));
        Assert.EndsWith("powershell.exe", runner.LastExecutable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-Command", runner.LastArguments!);
        Assert.Contains("-NoProfile", runner.LastArguments!);

        // El script va como UN argumento, con Add-AppxPackage y -DependencyPath.
        string script = Assert.Single(runner.LastArguments!, a => a.Contains("Add-AppxPackage"));
        Assert.Contains("-DependencyPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnaRutaConComillaSimple_QuedaEscapadaEnElScript()
    {
        // Defensivo: los nombres de archivo son fijos en EasyFix, pero si alguna vez una ruta
        // trajera un apóstrofe (perfil de usuario con comilla en el nombre, poco común), el
        // escaping de PowerShell tiene que duplicarla ('') en vez de romper el script o abrir una
        // fuga de comillas hacia el resto del comando.
        var downloader = new FakeDownloader { InjectSingleQuoteInPath = true };
        var runner = new FakeRunner();
        var locator = new SequencedLocator(null, WingetPath);

        await Build(downloader, runner, locator).EnsureAvailableAsync(null, CancellationToken.None);

        string script = runner.LastArguments!.Last();
        string rawPath = downloader.DownloadedPaths[0]; // el bundle, con la comilla inyectada
        string expectedEscaped = rawPath.Replace("'", "''", StringComparison.Ordinal);

        Assert.Contains(expectedEscaped, script, StringComparison.Ordinal);

        // Balance de comillas: cada apertura de literal PowerShell tiene su cierre. Si el escaping
        // fallara, la comilla de la ruta cerraría el literal antes de tiempo y el conteo daría impar.
        Assert.True(script.Count(c => c == '\'') % 2 == 0, $"Comillas sin balancear: {script}");
    }

    // ---- Fallos: nunca escapa una excepción, nunca inventa éxito -------------------------------

    [Fact]
    public async Task NoHayAssetEnElUltimoRelease_FalloClaro()
    {
        var downloader = new FakeDownloader { LatestUrl = null };
        var runner = new FakeRunner();

        WingetBootstrapResult result = await Build(downloader, runner, new SequencedLocator((string?)null))
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no se encontró", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(runner.LastExecutable); // nunca se llegó a ejecutar nada
    }

    [Fact]
    public async Task ResolverGitHubTiraExcepcion_NoPropaga_DevuelveFallo()
    {
        var downloader = new FakeDownloader { ResolveThrows = new HttpRequestException("DNS caído") };
        var runner = new FakeRunner();

        WingetBootstrapResult result = await Build(downloader, runner, new SequencedLocator((string?)null))
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("DNS caído", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescargaTiraExcepcion_NoPropaga_DevuelveFallo()
    {
        var downloader = new FakeDownloader { DownloadThrows = new IOException("disco lleno") };
        var runner = new FakeRunner();

        WingetBootstrapResult result = await Build(downloader, runner, new SequencedLocator((string?)null))
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("disco lleno", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAppxPackageTiraExcepcion_NoPropaga_DevuelveFallo()
    {
        // El hallazgo real: powershell.exe bloqueado por AppLocker/WDAC/EDR en un equipo
        // corporativo gestionado tira una excepción real, no un ProcessResult fallido. Antes esto
        // escapaba sin capturar.
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(throws: new System.ComponentModel.Win32Exception("Acceso denegado"));

        WingetBootstrapResult result = await Build(downloader, runner, new SequencedLocator((string?)null))
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Acceso denegado", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAppxPackageCorreSinErrorPeroWingetSigueSinAparecer_NoAfirmaExito()
    {
        // El principio central: código 0 no es la prueba. La prueba es volver a encontrarlo.
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(() => new ProcessResult(0, "Everything's fine", "", false, TimeSpan.Zero));
        var locator = new SequencedLocator(null, null); // nunca aparece

        WingetBootstrapResult result = await Build(downloader, runner, locator)
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Timeout_SeReportaComoTalYNoComoFalloGenerico()
    {
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(() => new ProcessResult(null, "", "", true, TimeSpan.FromMinutes(3)));
        var locator = new SequencedLocator(null, null);

        WingetBootstrapResult result = await Build(downloader, runner, locator)
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no terminó a tiempo", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StderrMencionaXaml_LoDiceExplicitoSinInventarUnCodigoSinVerificar()
    {
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(() => new ProcessResult(1, "",
            "Deployment failed: dependency Microsoft.UI.Xaml not found.", false, TimeSpan.Zero));
        var locator = new SequencedLocator(null, null);

        WingetBootstrapResult result = await Build(downloader, runner, locator)
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.Contains("Microsoft.UI.Xaml", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StderrGenerico_NoInventaQueFaltaXaml()
    {
        // Antes había un segundo heurístico ("9007") sin ninguna fuente verificada: podía dar
        // falso positivo con cualquier mensaje que por azar contuviera esos dígitos. Se sacó.
        var downloader = new FakeDownloader();
        var runner = new FakeRunner(() => new ProcessResult(1, "",
            "0x80070005 Access is denied.", false, TimeSpan.Zero));
        var locator = new SequencedLocator(null, null);

        WingetBootstrapResult result = await Build(downloader, runner, locator)
            .EnsureAvailableAsync(null, CancellationToken.None);

        Assert.DoesNotContain("Xaml", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access is denied", result.Detail, StringComparison.Ordinal);
    }
}
