using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El servicio de winget se testea entero acá: el proceso externo es un doble, así que se puede
/// verificar la construcción de argumentos, el orden y el manejo de fallos sin Windows.
/// </summary>
public sealed class WingetServiceTests
{
    private const string WingetPath = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.2\winget.exe";

    /// <summary>Registra cada invocación y devuelve el resultado que se le indique por paquete.</summary>
    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, ProcessResult> _respond;

        public FakeRunner(Func<IReadOnlyList<string>, ProcessResult>? respond = null) =>
            _respond = respond ?? (_ => new ProcessResult(0, "Successfully installed", "", false, TimeSpan.Zero));

        /// <summary>La tabla de códigos no existe en el fake: se responde como comando no soportado.</summary>
        private static bool IsErrorTableQuery(IReadOnlyList<string> args) =>
            args.Count > 0 && args[0] == "error";

        public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = new();

        /// <summary>
        /// Solo las invocaciones de instalación. El servicio también corre «winget error --output»
        /// una vez por tanda para cargar la tabla de códigos del equipo, y «source reset» cuando el
        /// catálogo está roto: esas no son instalaciones.
        /// </summary>
        public List<(string Exe, IReadOnlyList<string> Args)> InstallCalls =>
            Calls.Where(c => c.Args.Count > 0 && c.Args[0] == "install").ToList();

        /// <summary>Cuántas invocaciones hay en vuelo a la vez. Debe ser siempre 1.</summary>
        public int MaxConcurrent { get; private set; }

        private int _current;

        public async Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            Calls.Add((executablePath, arguments));

            if (IsErrorTableQuery(arguments))
            {
                // Se simula una versión de winget sin el comando: el servicio cae en las constantes.
                return new ProcessResult(1, "", "Unknown command", false, TimeSpan.Zero);
            }

            int now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);

            await Task.Yield();

            Interlocked.Decrement(ref _current);
            return _respond(arguments);
        }
    }

    private sealed class FakeLocator : IWingetLocator
    {
        private readonly string? _path;
        public FakeLocator(string? path) => _path = path;
        public string? Find() => _path;
    }

    private static WingetPackage Package(string id, string name) =>
        new() { Id = id, Name = name };

    /// <summary>Descargador que nunca se usa: estos tests solo ejercitan la vía de winget.</summary>
    private sealed class UnusedDownloader : IFileDownloader
    {
        public Task<string> DownloadAsync(Uri url, string fileName, IProgress<string>? p, CancellationToken ct) =>
            throw new InvalidOperationException("No debería descargarse nada en estos tests.");

        public Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct) =>
            throw new InvalidOperationException("No debería resolverse nada en estos tests.");
    }

    private static WingetService Build(IProcessRunner runner, string? wingetPath = WingetPath) =>
        new(runner,
            new FakeLocator(wingetPath),
            new DirectDownloadInstaller(
                new UnusedDownloader(), runner, new ThresholdOptions(),
                NullLogger<DirectDownloadInstaller>.Instance),
            new ThresholdOptions(),
            NullLogger<WingetService>.Instance);

    // ---- Construcción de argumentos ---------------------------------------------------------

    [Fact]
    public async Task InstalaConLosArgumentosCorrectos()
    {
        var runner = new FakeRunner();

        await Build(runner).InstallAsync(new[] { Package("RustDesk.RustDesk", "RustDesk") });

        (string exe, IReadOnlyList<string> args) = Assert.Single(runner.InstallCalls);

        Assert.Equal(WingetPath, exe);
        Assert.Equal(
            new[]
            {
                "install",
                "--id", "RustDesk.RustDesk",
                "--exact",
                "--source", "winget",
                "--silent",
                "--disable-interactivity",
                "--accept-package-agreements",
                "--accept-source-agreements",
            },
            args);
    }

    [Fact]
    public async Task UsaExact_ParaQueUnIdParcialNoTraigaOtroPaquete()
    {
        var runner = new FakeRunner();
        await Build(runner).InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        Assert.Contains("--exact", runner.InstallCalls[0].Args);
    }

    [Fact]
    public async Task FijaLaFuenteOficial_YNoUnaAgregadaPorElUsuario()
    {
        var runner = new FakeRunner();
        await Build(runner).InstallAsync(new[] { Package("Google.Chrome", "Chrome") });

        IReadOnlyList<string> args = runner.InstallCalls[0].Args;
        int i = args.ToList().IndexOf("--source");
        Assert.True(i >= 0);
        Assert.Equal("winget", args[i + 1]);
    }

    [Fact]
    public async Task DesactivaLaInteractividad_ParaQueNingunInstaladorEspereUnEnter()
    {
        var runner = new FakeRunner();
        await Build(runner).InstallAsync(new[] { Package("Google.Chrome", "Chrome") });

        Assert.Contains("--disable-interactivity", runner.InstallCalls[0].Args);
    }

    // ---- En serie, nunca en paralelo --------------------------------------------------------

    [Fact]
    public async Task InstalaEnSerie()
    {
        // Dos instaladores de Windows a la vez se pelean por el mutex de Windows Installer: uno
        // falla, o peor, queda a medias.
        var runner = new FakeRunner();

        var packages = new[]
        {
            Package("Google.Chrome", "Chrome"),
            Package("7zip.7zip", "7-Zip"),
            Package("RustDesk.RustDesk", "RustDesk"),
        };

        await Build(runner).InstallAsync(packages);

        Assert.Equal(3, runner.InstallCalls.Count);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task ConservaElOrdenDeLaLista()
    {
        var runner = new FakeRunner();

        await Build(runner).InstallAsync(new[]
        {
            Package("A.A", "A"),
            Package("B.B", "B"),
            Package("C.C", "C"),
        });

        Assert.Equal(
            new[] { "A.A", "B.B", "C.C" },
            runner.InstallCalls.Select(c => c.Args[c.Args.ToList().IndexOf("--id") + 1]));
    }

    // ---- Resultados ------------------------------------------------------------------------

    [Fact]
    public async Task DevuelveUnResultadoPorPaquete()
    {
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(runner).InstallAsync(new[]
        {
            Package("A.A", "A"),
            Package("B.B", "B"),
        });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.PackageAvailable));
    }

    [Fact]
    public async Task UnPaqueteQueFalla_NoCortaLaTanda()
    {
        var runner = new FakeRunner(args =>
            args.Contains("Roto.Roto")
                ? new ProcessResult(unchecked((int)0x80070005), "", "Access is denied.", false, TimeSpan.Zero)
                : new ProcessResult(0, "Successfully installed", "", false, TimeSpan.Zero));

        IReadOnlyList<WingetResult> results = await Build(runner).InstallAsync(new[]
        {
            Package("Roto.Roto", "Roto"),
            Package("Bueno.Bueno", "Bueno"),
        });

        Assert.Equal(2, results.Count);
        Assert.Equal(WingetOutcome.Failed, results[0].Outcome);
        Assert.Equal(WingetOutcome.Installed, results[1].Outcome);
    }

    [Fact]
    public async Task UnRunnerQueRevienta_SeConvierteEnFalloDeEsePaquete()
    {
        var runner = new ThrowingRunner();

        IReadOnlyList<WingetResult> results = await Build(runner).InstallAsync(new[]
        {
            Package("A.A", "A"),
            Package("B.B", "B"),
        });

        // Los dos fallan, pero la tanda completa igual devolvió resultados.
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(WingetOutcome.Failed, r.Outcome));
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default) =>
            throw new System.ComponentModel.Win32Exception("El proceso no se pudo iniciar.");
    }

    // ---- winget ausente e IDs inválidos -----------------------------------------------------

    [Fact]
    public async Task SinWinget_DevuelveWingetMissing_YNoEjecutaNada()
    {
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(runner, wingetPath: null)
            .InstallAsync(new[] { Package("A.A", "A"), Package("B.B", "B") });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(WingetOutcome.WingetMissing, r.Outcome));
        Assert.Empty(runner.InstallCalls);
    }

    [Fact]
    public async Task IdInvalido_SeRechazaSinEjecutarWinget()
    {
        // ArgumentList ya impide la inyección, pero un ID basura tiene que fallar con un mensaje
        // claro y no con un error raro de winget.
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(runner)
            .InstallAsync(new[] { Package("Chrome && calc.exe", "Malicioso") });

        WingetResult result = Assert.Single(results);
        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.Contains("no es válido", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.InstallCalls);
    }

    [Fact]
    public async Task IdInvalidoEnElMedio_NoImpideInstalarLosDemas()
    {
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(runner).InstallAsync(new[]
        {
            Package("Bueno.Uno", "Uno"),
            Package("mal | id", "Malo"),
            Package("Bueno.Dos", "Dos"),
        });

        Assert.Equal(3, results.Count);
        Assert.Equal(WingetOutcome.Installed, results[0].Outcome);
        Assert.Equal(WingetOutcome.Failed, results[1].Outcome);
        Assert.Equal(WingetOutcome.Installed, results[2].Outcome);
        Assert.Equal(2, runner.InstallCalls.Count); // el inválido no llegó a ejecutarse
    }

    // ---- Progreso y cancelación -------------------------------------------------------------

    [Fact]
    public async Task InformaElProgresoAntesYDespuesDeCadaPaquete()
    {
        var runner = new FakeRunner();
        var reported = new List<InstallProgress>();
        var progress = new Progress<InstallProgress>(reported.Add);

        await Build(runner).InstallAsync(
            new[] { Package("A.A", "A"), Package("B.B", "B") }, progress);

        for (int i = 0; i < 60 && reported.Count < 4; i++)
        {
            await Task.Delay(10);
        }

        // Dos avisos por paquete: uno al empezar (Finished null) y uno al terminar.
        Assert.Equal(4, reported.Count);
        Assert.Null(reported[0].Finished);
        Assert.NotNull(reported[1].Finished);
        Assert.Equal(1, reported[0].Index);
        Assert.Equal(2, reported[0].Total);
    }

    [Fact]
    public async Task Cancelacion_SePropaga()
    {
        var runner = new FakeRunner();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Build(runner).InstallAsync(new[] { Package("A.A", "A") }, null, cts.Token));
    }

    [Fact]
    public async Task ListaVacia_NoEjecutaNada()
    {
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(runner).InstallAsync(Array.Empty<WingetPackage>());

        Assert.Empty(results);
        Assert.Empty(runner.InstallCalls);
    }

    // ---- Configuración real -----------------------------------------------------------------

    [Fact]
    public void RustDeskEstaConfigurado_YMarcadoPorDefecto()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        WingetPackage rustDesk = Assert.Single(
            options.WingetPackages, p => p.Name.Contains("RustDesk", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("RustDesk.RustDesk", rustDesk.Id);
        Assert.True(rustDesk.IsDefault);
        Assert.True(WingetPackageId.IsValid(rustDesk.Id));
    }

    [Fact]
    public void NoHayIdsDuplicadosEnLaConfiguracion()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        var duplicates = options.WingetPackages
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }
}
