using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Network;
using EasyFix.Core.Tests.Fakes;
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
            CancellationToken ct = default,
            IReadOnlyCollection<int>? benignExitCodes = null)
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

    /// <summary>
    /// Para los tests de bootstrap: empieza sin winget y permite simular que apareció después de
    /// instalarlo, igual que pasaría en un equipo real tras correr <c>Add-AppxPackage</c>.
    /// </summary>
    private sealed class MutableLocator : IWingetLocator
    {
        public string? Path { get; set; }
        public string? Find() => Path;
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

        public Task<string?> FetchTextAsync(Uri url, CancellationToken ct) =>
            throw new InvalidOperationException("No debería consultarse ningún origen en estos tests.");
    }

    private static WingetService Build(
        IProcessRunner runner,
        string? wingetPath = WingetPath,
        IConnectivityCheck? connectivity = null,
        IWingetLocator? locator = null,
        IWingetBootstrapper? bootstrapper = null) =>
        new(runner,
            locator ?? new FakeLocator(wingetPath),
            new DirectDownloadInstaller(
                new UnusedDownloader(), runner, new ThresholdOptions(),
                NullLogger<DirectDownloadInstaller>.Instance),
            // Por defecto con internet: estos tests ejercitan la vía de winget, y la compuerta de red
            // tiene sus propios tests más abajo.
            connectivity ?? FakeConnectivityCheck.Online(),
            // winget ya está en casi todos estos tests: el bootstrap nunca debería dispararse, y
            // Forbidden() lo deja en evidencia si alguna vez pasa.
            bootstrapper ?? FakeWingetBootstrapper.Forbidden(),
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
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken ct = default, IReadOnlyCollection<int>? benignExitCodes = null) =>
            throw new System.ComponentModel.Win32Exception("El proceso no se pudo iniciar.");
    }

    // ---- winget ausente e IDs inválidos -----------------------------------------------------

    [Fact]
    public async Task SinWinget_IntentaBootstrapYSiSigueFaltando_DevuelveWingetMissing_YNoEjecutaNada()
    {
        var runner = new FakeRunner();
        var bootstrapper = FakeWingetBootstrapper.Fails("no se pudo.");

        IReadOnlyList<WingetResult> results = await Build(
                runner, wingetPath: null, bootstrapper: bootstrapper)
            .InstallAsync(new[] { Package("A.A", "A"), Package("B.B", "B") });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(WingetOutcome.WingetMissing, r.Outcome));
        Assert.Empty(runner.InstallCalls);
        Assert.Equal(1, bootstrapper.Calls); // uno por corrida, no uno por paquete
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

    // ---- Bootstrap de winget cuando falta --------------------------------------------------

    [Fact]
    public async Task SinWinget_IntentaElBootstrapYDespuesInstalaNormal()
    {
        var locator = new MutableLocator { Path = null };
        var runner = new FakeRunner();

        // Simula lo que pasaría de verdad: EnsureAvailableAsync corre Add-AppxPackage y, si
        // funcionó, winget.exe ya está en el disco — por eso el locator "encuentra" la ruta recién
        // ACÁ, como efecto del bootstrap, no antes.
        var bootstrapper = new FakeWingetBootstrapper(() =>
        {
            locator.Path = WingetPath;
            return new WingetBootstrapResult(true, "winget instalado.");
        });

        IReadOnlyList<WingetResult> results = await Build(
                runner, locator: locator, bootstrapper: bootstrapper)
            .InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        WingetResult result = Assert.Single(results);
        Assert.Equal(WingetOutcome.Installed, result.Outcome);
        Assert.Equal(1, bootstrapper.Calls);
        Assert.Single(runner.InstallCalls); // llegó a instalar de verdad, no se quedó en el bootstrap
    }

    [Fact]
    public async Task BootstrapFalla_ElResultadoExplicaQueSeIntentoYNoSePudo()
    {
        var locator = new MutableLocator { Path = null }; // sigue sin aparecer
        var runner = new FakeRunner();
        var bootstrapper = FakeWingetBootstrapper.Fails(
            "no se pudo instalar winget: falta Microsoft.UI.Xaml.");

        IReadOnlyList<WingetResult> results = await Build(
                runner, locator: locator, bootstrapper: bootstrapper)
            .InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        WingetResult result = Assert.Single(results);
        Assert.Equal(WingetOutcome.WingetMissing, result.Outcome);
        Assert.Contains("se intentó", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.UI.Xaml", result.Detail, StringComparison.Ordinal);
        Assert.Empty(runner.InstallCalls); // nunca se llegó a lanzar winget: no existe
    }

    [Fact]
    public async Task BootstrapFalla_SoloSeIntentaUnaVezPorCorrida_NoUnaPorPaquete()
    {
        var locator = new MutableLocator { Path = null };
        var runner = new FakeRunner();
        var bootstrapper = FakeWingetBootstrapper.Fails("no se pudo.");

        await Build(runner, locator: locator, bootstrapper: bootstrapper)
            .InstallAsync(new[] { Package("7zip.7zip", "7-Zip"), Package("VideoLAN.VLC", "VLC") });

        Assert.Equal(1, bootstrapper.Calls);
    }

    [Fact]
    public async Task WingetYaDisponible_NuncaLlamaAlBootstrap()
    {
        // Build() por defecto usa FakeWingetBootstrapper.Forbidden(): si este test pasa, es porque
        // nadie lo invocó. Lo explícito acá es documentar la intención.
        var runner = new FakeRunner();

        await Build(runner).InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });
    }

    [Fact]
    public async Task SinInternetYSinWinget_LaCompuertaDeRedGanaYNuncaLlamaAlBootstrap()
    {
        // La compuerta de red se evalúa ANTES del loop de paquetes, así que estructuralmente no
        // hay forma de llegar al bootstrap sin internet. Este test fija esa garantía con código,
        // no solo con la lectura del fuente.
        var locator = new MutableLocator { Path = null };
        var runner = new FakeRunner();

        IReadOnlyList<WingetResult> results = await Build(
                runner,
                locator: locator,
                bootstrapper: FakeWingetBootstrapper.Forbidden(),
                connectivity: FakeConnectivityCheck.Offline())
            .InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        Assert.Single(results);
        Assert.Empty(runner.InstallCalls);
    }

    [Fact]
    public async Task BootstrapperQueRevienta_NoAbortaLaTanda_SeTrataComoFalloDeEsePaquete()
    {
        // Hallazgo real de una revisión adversarial: si IWingetBootstrapper tira una excepción en
        // vez de devolver un resultado (ej. powershell.exe bloqueado por AppLocker/EDR en un
        // equipo corporativo), eso NO puede cortar la tanda entera — un técnico instalando cuatro
        // programas no puede quedarse sin ninguno por un fallo que solo afecta a winget.
        var locator = new MutableLocator { Path = null };
        var runner = new FakeRunner();
        var bootstrapper = new FakeWingetBootstrapper(
            () => throw new InvalidOperationException("powershell.exe bloqueado por política."));

        IReadOnlyList<WingetResult> results = await Build(
                runner, locator: locator, bootstrapper: bootstrapper)
            .InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        WingetResult result = Assert.Single(results);
        Assert.Equal(WingetOutcome.WingetMissing, result.Outcome);
    }

    [Fact]
    public async Task BootstrapExitoso_QuedaUnaNotaVisibleParaElReporte()
    {
        // Antes: si el bootstrap funcionaba, el paquete instalado no mostraba ningún detalle (el
        // motivo solo se ve cuando algo falla) y la instalación de winget quedaba 100 % muda para
        // quien mira la pantalla. LastBootstrapNote es lo que MainViewModel antepone al resumen.
        var locator = new MutableLocator { Path = null };
        var runner = new FakeRunner();
        var bootstrapper = new FakeWingetBootstrapper(() =>
        {
            locator.Path = WingetPath;
            return new WingetBootstrapResult(true, "winget instalado.");
        });

        WingetService service = Build(runner, locator: locator, bootstrapper: bootstrapper);
        Assert.Null(service.LastBootstrapNote); // nada todavía: no corrió

        await service.InstallAsync(new[] { Package("7zip.7zip", "7-Zip") });

        Assert.NotNull(service.LastBootstrapNote);
        Assert.Contains("instaló automáticamente", service.LastBootstrapNote, StringComparison.Ordinal);
    }

    // ---- Actualizar lo instalado ------------------------------------------------------------

    [Fact]
    public void SaysNothingToUpgrade_SoloMiraElTexto_NuncaElCodigoDeSalida()
    {
        // La regla central del proyecto aplicada acá: un código 0 significa «el comando terminó
        // bien», y eso cubre tanto «no había nada» como «actualicé cuatro programas». Deducir
        // «todo estaba al día» de un 0 sería afirmar algo que no se midió.
        Assert.False(WingetErrorCodes.SaysNothingToUpgrade(null));
        Assert.False(WingetErrorCodes.SaysNothingToUpgrade(""));
        Assert.False(WingetErrorCodes.SaysNothingToUpgrade("Successfully installed Chrome"));
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("No se encontró ningún paquete instalado que coincida")]
    [InlineData("No applicable update found")]
    [InlineData("No hay actualizaciones disponibles")]
    public void SaysNothingToUpgrade_ReconoceElTextoEnLosDosIdiomas(string output) =>
        Assert.True(WingetErrorCodes.SaysNothingToUpgrade(output));

    [Fact]
    public async Task UpgradeAll_SinInternet_NoLanzaNingunProceso()
    {
        var runner = new FakeRunner();

        WingetUpgradeResult result = await Build(runner, connectivity: FakeConnectivityCheck.Offline())
            .UpgradeAllAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("internet", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpgradeAll_SinWinget_LoDiceSinIntentarNada()
    {
        var runner = new FakeRunner();

        WingetUpgradeResult result = await Build(runner, wingetPath: null).UpgradeAllAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("winget", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UpgradeAll_UsaUpgradeAllEnSilencio()
    {
        var runner = new FakeRunner();

        await Build(runner).UpgradeAllAsync();

        (string _, IReadOnlyList<string> args) = Assert.Single(
            runner.Calls, c => c.Args.Count > 0 && c.Args[0] == "upgrade");

        Assert.Contains("--all", args);
        Assert.Contains("--silent", args);
        Assert.Contains("--disable-interactivity", args);

        // --include-unknown queda AFUERA a propósito: actualiza paquetes cuya versión instalada
        // winget no pudo determinar, o sea sin saber desde dónde se parte.
        Assert.DoesNotContain("--include-unknown", args);
    }

    [Fact]
    public async Task UpgradeAll_ExitoSinTextoDeNada_NoAfirmaQueTodoEstabaAlDia()
    {
        var runner = new FakeRunner(_ =>
            new ProcessResult(0, "Successfully upgraded 3 packages", "", false, TimeSpan.Zero));

        WingetUpgradeResult result = await Build(runner).UpgradeAllAsync();

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("al día", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpgradeAll_CuandoWingetDiceQueNoHayNada_LoReportaComoExito()
    {
        var runner = new FakeRunner(_ =>
            new ProcessResult(0, "No installed package found matching input criteria.", "", false, TimeSpan.Zero));

        WingetUpgradeResult result = await Build(runner).UpgradeAllAsync();

        Assert.True(result.Succeeded);
        Assert.Contains("al día", result.Detail, StringComparison.OrdinalIgnoreCase);
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
    public void TodoProgramaTieneSuHistoria()
    {
        // La historia es lo que ve el cliente mientras espera. Un programa sin historia deja la
        // pantalla vacía justo cuando hay que mirarla, y es facilísimo olvidarla al agregar uno
        // nuevo — por eso lo verifica un test y no la buena memoria.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        var sinHistoria = options.WingetPackages
            .Where(p => string.IsNullOrWhiteSpace(p.Story))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            sinHistoria.Count == 0,
            $"Estos programas no tienen 'story' en appsettings.json: {string.Join(", ", sinHistoria)}. " +
            "Agregale una (un hecho real y por qué se eligió ese programa) antes de publicar.");
    }

    [Fact]
    public void TodoProgramaTieneMonograma()
    {
        // El monograma (círculo con el color de la marca y su inicial) acompaña a la historia.
        // Sin color configurado cae al acento del tema y todos los programas se ven iguales, que
        // es justo lo contrario de lo que el monograma busca: que se reconozca de un vistazo.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        var sinColor = options.WingetPackages
            .Where(p => string.IsNullOrWhiteSpace(p.BrandColor))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            sinColor.Count == 0,
            $"Sin 'brandColor': {string.Join(", ", sinColor)}. Agregá el color de la marca en #RRGGBB.");
    }

    [Theory]
    [InlineData("#4285F4")]
    [InlineData("#1E7D34")]
    public void ElFormatoDeColorEsElEsperado(string sample) =>
        Assert.Matches("^#[0-9A-Fa-f]{6}$", sample);

    [Fact]
    public void LosColoresDeMarcaSonHexValidos()
    {
        // Un valor inválido no rompe la app (el converter cae al acento), pero deja el programa
        // sin su color sin que nadie se entere. Mejor que falle acá.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.BrandColor is not null))
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", package.BrandColor!);
        }
    }

    [Fact]
    public void LasInicialesSonUnSoloCaracter()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.BrandInitial is not null))
        {
            Assert.True(
                package.BrandInitial!.Length == 1,
                $"La inicial de {package.Name} es '{package.BrandInitial}'; tiene que ser un solo carácter.");
        }
    }

    [Fact]
    public void LasHistoriasTienenLargoUtil()
    {
        // Demasiado corta no ocupa el tiempo de espera; demasiado larga no se lee. El rango sale
        // de que la pantalla le da ~560 px de ancho y varias líneas de alto.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.Story is not null))
        {
            int length = package.Story!.Length;

            Assert.True(
                length is >= 120 and <= 600,
                $"La historia de {package.Name} tiene {length} caracteres; se esperan entre 120 y 600.");
        }
    }

    [Fact]
    public void TodaDescargaDirectaSabeComoDetectarSiYaEstaInstalada()
    {
        // Sin este dato, un paquete de descarga directa se reinstala SIEMPRE: es el bug que se
        // reportó con Chrome. Los que van por winget no lo necesitan — winget lo detecta solo.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        var sinDeteccion = options.WingetPackages
            .Where(p => p.Direct is not null)
            .Where(p => string.IsNullOrWhiteSpace(p.Direct!.DetectDisplayName))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            sinDeteccion.Count == 0,
            $"Estas descargas directas no tienen 'detectDisplayName': {string.Join(", ", sinDeteccion)}. " +
            "Sin eso se reinstalan aunque ya estén.");
    }

    [Fact]
    public void LosNombresDeDeteccionSonEspecificos()
    {
        // Un nombre corto o genérico haría que EasyFix crea que algo está instalado cuando no lo
        // está, y lo saltee — el fallo sería silencioso y en la dirección peligrosa: el cliente
        // se queda sin el programa.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.Direct?.DetectDisplayName is not null))
        {
            string name = package.Direct!.DetectDisplayName!;

            Assert.True(
                name.Trim().Length >= 5,
                $"'{name}' (de {package.Name}) es demasiado corto para identificar un programa " +
                "en el registro sin matchear otra cosa.");
        }
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
