// System.IO explícito: el SDK de WPF no lo trae en los implicit usings, a diferencia del SDK
// normal que usa el proyecto de tests de Core.
using System.IO;
using EasyFix.App.ViewModels;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Classification;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Drivers;
using EasyFix.Core.Fixes;
using EasyFix.Core.Network;
using EasyFix.Core.Recommendations;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyFix.App.Tests;

/// <summary>
/// Arma un <see cref="MainViewModel"/> real con dobles para todo lo que toca Windows, para poder
/// manejarlo <b>como lo maneja una persona</b>: apretando comandos y mirando qué estado queda.
/// </summary>
/// <remarks>
/// <para>Es la capa que faltaba. Los tests de <c>EasyFix.Core</c> cubren cada pieza por separado y
/// pasaban en verde mientras la app, en el equipo de un cliente, quedaba con todos los botones
/// muertos después de un fallo de instalación. Eso no es un bug de ninguna pieza: es del
/// pegamento, y solo aparece recorriendo el flujo entero.</para>
///
/// <para>Lo que NO se puede probar acá: todo lo que exige Windows de verdad —WMI, registro, COM,
/// instaladores, render—. <c>SystemProbe</c> se construye pero nunca se invoca; los flujos que
/// dependen de haber analizado se prueban por su contrato, no por su resultado.</para>
/// </remarks>
internal static class Harness
{
    /// <summary>Instalador que falla siempre, para reproducir el escenario que rompió la UI.</summary>
    internal sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken ct = default, IReadOnlyCollection<int>? benignExitCodes = null) =>
            // 1639 = ERROR_INVALID_COMMAND_LINE, el código real con el que falló RustDesk.
            Task.FromResult(new ProcessResult(1639, "", "Argumento inválido.", false, TimeSpan.Zero));
    }

    internal sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken ct = default, IReadOnlyCollection<int>? benignExitCodes = null) =>
            throw new InvalidOperationException("El proceso no se pudo lanzar.");
    }

    internal sealed class OkRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken ct = default, IReadOnlyCollection<int>? benignExitCodes = null) =>
            Task.FromResult(new ProcessResult(0, "Successfully installed", "", false, TimeSpan.Zero));
    }

    private sealed class FakeLocator : IWingetLocator
    {
        private readonly string? _path;
        public FakeLocator(string? path) => _path = path;
        public string? Find() => _path;
    }

    private sealed class FakeDownloader : IFileDownloader
    {
        public Task<string> DownloadAsync(Uri url, string fileName, IProgress<string>? p, CancellationToken ct)
        {
            string path = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid()}-{fileName}");
            File.WriteAllText(path, "x");
            return Task.FromResult(path);
        }

        public Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct) =>
            Task.FromResult<Uri?>(new Uri("https://example.invalid/pkg.msi"));
    }

    private sealed class FakeConnectivity : IConnectivityCheck
    {
        private readonly bool _online;
        public FakeConnectivity(bool online) => _online = online;

        public Task<ConnectivityResult> CheckAsync(CancellationToken ct) =>
            Task.FromResult(_online
                ? ConnectivityResult.Online()
                : new ConnectivityResult(ConnectivityStatus.DnsFailed, "Sin internet (fake).", "fake"));
    }

    private sealed class NoPrograms : IInstalledProgramsReader
    {
        public IReadOnlyList<InstalledProgram> Read() => Array.Empty<InstalledProgram>();
    }

    private sealed class NoBootstrap : IWingetBootstrapper
    {
        public Task<WingetBootstrapResult> EnsureAvailableAsync(IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new WingetBootstrapResult(true, "winget disponible (fake)."));
    }

    private sealed class NoDrivers : IDriverCatalog
    {
        public Task<DriverSearchResult> SearchAsync(CancellationToken ct) =>
            Task.FromResult(DriverSearchResult.Ok(Array.Empty<DriverUpdate>()));

        public Task<DriverInstallResult> InstallAsync(
            IReadOnlyList<string> updateIds, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new DriverInstallResult(true, false, "ok"));
    }

    private sealed class NoRestorePoints : IRestorePointService
    {
        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) =>
            Task.FromResult(new RestorePointResult(Sequence: 1));
    }

    private sealed class NoBitLocker : IBitLockerService
    {
        public Task<bool> SuspendUntilNextRebootAsync(CancellationToken ct) => Task.FromResult(true);
    }

    /// <summary>
    /// Construye el ViewModel real. <paramref name="runner"/> decide si las instalaciones salen
    /// bien, fallan o revientan.
    /// </summary>
    public static MainViewModel Build(IProcessRunner runner, bool online = true)
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(FindAppSettings()));
        var thresholds = options.Thresholds;
        var connectivity = new FakeConnectivity(online);

        var direct = new DirectDownloadInstaller(
            new FakeDownloader(), runner, thresholds,
            NullLogger<DirectDownloadInstaller>.Instance, new NoPrograms());

        var winget = new WingetService(
            runner,
            new FakeLocator(@"C:\winget.exe"),
            direct,
            connectivity,
            new NoBootstrap(),
            thresholds,
            NullLogger<WingetService>.Instance);

        var fixRunner = new FixRunner(
            new NoRestorePoints(), new NoBitLocker(), NullLogger<FixRunner>.Instance);

        return new MainViewModel(
            options,
            new SystemProbe(NullLogger<SystemProbe>.Instance),
            new HardwareAdvisor(thresholds),
            new NoPrograms(),
            new DefaultAppsConfigurator(runner, NullLogger<DefaultAppsConfigurator>.Instance),
            new StartupEntryReader(NullLogger<StartupEntryReader>.Instance),
            new StartupClassifier(options.Classifier),
            winget,
            connectivity,
            fixRunner,
            new IFix[] { new DriverUpdateFix(new NoDrivers(), NullLogger<DriverUpdateFix>.Instance) },
            new UndoEngine(Array.Empty<IUndoHandler>(), NullLogger<UndoEngine>.Instance),
            new JournalStore(NullLogger<JournalStore>.Instance),
            NullLogger<MainViewModel>.Instance);
    }

    private static string FindAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("No se encontró appsettings.json.");
    }
}
