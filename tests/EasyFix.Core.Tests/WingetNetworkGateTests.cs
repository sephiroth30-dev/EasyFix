using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Network;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// La compuerta de red de <see cref="WingetService"/>.
/// </summary>
/// <remarks>
/// <para>Reconstruye el escenario del log del 2026-08-29 (equipo 20-b250la, sin internet): EasyFix
/// intentó instalar cuatro programas, falló los cuatro, y en el log culpó tres veces a la sesión
/// elevada de un problema de catálogo que en realidad era falta de red. Además declaró «Fuentes de
/// winget reparadas» ocho segundos antes de que el mismo error volviera.</para>
///
/// <para>Lo que estos tests fijan: sin internet <b>no se lanza ningún proceso</b>. Es la única forma
/// de que el diagnóstico no pueda equivocarse, porque no hay error de winget que malinterpretar.</para>
/// </remarks>
public sealed class WingetNetworkGateTests
{
    private const string WingetPath = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.2\winget.exe";

    /// <summary>Runner que falla el test si alguien lo invoca.</summary>
    private sealed class ForbiddenRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct,
            IReadOnlyCollection<int>? successExitCodes = null) =>
            throw new InvalidOperationException(
                $"No se debería haber lanzado ningún proceso sin internet, y se lanzó: {executable}");
    }

    private sealed class FakeLocator : IWingetLocator
    {
        public string? Find() => WingetPath;
    }

    private sealed class ForbiddenDownloader : IFileDownloader
    {
        public Task<string> DownloadAsync(Uri url, string fileName, IProgress<string>? p, CancellationToken ct) =>
            throw new InvalidOperationException("No se debería haber descargado nada sin internet.");

        public Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct) =>
            throw new InvalidOperationException("No se debería haber consultado GitHub sin internet.");
    }

    private static WingetService Build(IConnectivityCheck connectivity)
    {
        var runner = new ForbiddenRunner();

        return new WingetService(
            runner,
            new FakeLocator(),
            new DirectDownloadInstaller(
                new ForbiddenDownloader(), runner, new ThresholdOptions(),
                NullLogger<DirectDownloadInstaller>.Instance),
            connectivity,
            new ThresholdOptions(),
            NullLogger<WingetService>.Instance);
    }

    /// <summary>Los cuatro paquetes que se intentaron en el equipo del log.</summary>
    private static IReadOnlyList<WingetPackage> LogPackages() => new WingetPackage[]
    {
        new()
        {
            Id = "Google.Chrome",
            Name = "Google Chrome",
            // Chrome va por descarga directa: es el que falló con SocketException 11001.
            Direct = new DirectDownload
            {
                Url = "https://dl.google.com/chrome/install/googlechromestandaloneenterprise64.msi",
            },
        },
        new() { Id = "Adobe.Acrobat.Reader.64-bit", Name = "Adobe Acrobat Reader" },
        new() { Id = "7zip.7zip", Name = "7-Zip" },
        new() { Id = "Microsoft.VCRedist.2015+.x64", Name = "Visual C++ Redist (x64)" },
    };

    [Fact]
    public async Task SinInternet_NoSeLanzaNingunProceso()
    {
        // ForbiddenRunner y ForbiddenDownloader revientan si se los usa: que el test pase ES la
        // verificación de que la compuerta cortó antes.
        WingetService service = Build(FakeConnectivityCheck.Offline());

        IReadOnlyList<WingetResult> results = await service.InstallAsync(LogPackages());

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(WingetOutcome.NoNetwork, r.Outcome));
    }

    [Fact]
    public async Task SinInternet_TambienCortaLaDescargaDirecta()
    {
        // Chrome no pasa por winget: si la compuerta solo cubriera winget, este seguiría fallando con
        // «Host desconocido» en crudo, que es exactamente lo que pasaba.
        WingetService service = Build(FakeConnectivityCheck.Offline());

        IReadOnlyList<WingetResult> results = await service.InstallAsync(LogPackages());

        WingetResult chrome = results.Single(r => r.PackageId == "Google.Chrome");
        Assert.Equal(WingetOutcome.NoNetwork, chrome.Outcome);
    }

    [Fact]
    public async Task SinInternet_NingunPaqueteCuentaComoDisponible()
    {
        WingetService service = Build(FakeConnectivityCheck.Offline());

        IReadOnlyList<WingetResult> results = await service.InstallAsync(LogPackages());

        Assert.All(results, r => Assert.False(r.PackageAvailable));
    }

    [Fact]
    public async Task SinInternet_ElDetalleExplicaQueEsLaRedYNoWinget()
    {
        WingetService service = Build(FakeConnectivityCheck.Offline());

        IReadOnlyList<WingetResult> results = await service.InstallAsync(LogPackages());

        foreach (WingetResult result in results)
        {
            Assert.Contains("internet", result.Detail, StringComparison.OrdinalIgnoreCase);

            // Lo que NO puede aparecer: la explicación de la sesión elevada. Ese texto fue el que
            // mandó al técnico a buscar un problema de permisos que no existía.
            Assert.DoesNotContain("elevada", result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("catálogo", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LaComprobacionDeRedSeHaceUnaSolaVezPorTanda()
    {
        // Diez paquetes con una comprobación de 8 s cada uno serían 80 s antes de instalar nada.
        var connectivity = FakeConnectivityCheck.Offline();
        WingetService service = Build(connectivity);

        await service.InstallAsync(LogPackages());

        Assert.Equal(1, connectivity.Calls);
    }

    [Fact]
    public async Task SinPaquetes_NoSeComprueba()
    {
        // No tiene sentido esperar 8 s para no instalar nada.
        var connectivity = FakeConnectivityCheck.Offline();
        WingetService service = Build(connectivity);

        IReadOnlyList<WingetResult> results = await service.InstallAsync(Array.Empty<WingetPackage>());

        Assert.Empty(results);
        Assert.Equal(0, connectivity.Calls);
    }

    [Fact]
    public async Task SinInternet_ElProgresoReportaCadaPaqueteComoTerminado()
    {
        // Si la compuerta devolviera sin reportar, la barra quedaría clavada y la lista de programas
        // no mostraría estado en ninguno.
        var reported = new List<InstallProgress>();
        WingetService service = Build(FakeConnectivityCheck.Offline());

        await service.InstallAsync(LogPackages(), new Progress<InstallProgress>(reported.Add));

        // Progress<T> despacha por el contexto de sincronización; sin uno, va al pool.
        await Task.Delay(100);

        Assert.Equal(4, reported.Count(p => p.Finished is not null));
        Assert.Equal(4, reported.Where(p => p.Finished is not null).Max(p => p.Index));
    }

    [Theory]
    [InlineData(ConnectivityStatus.NoAdapter)]
    [InlineData(ConnectivityStatus.DnsFailed)]
    [InlineData(ConnectivityStatus.Unreachable)]
    [InlineData(ConnectivityStatus.CaptivePortal)]
    [InlineData(ConnectivityStatus.Unknown)]
    public async Task CualquierEstadoQueNoSeaOnline_CortaLaTanda(ConnectivityStatus status)
    {
        // Unknown incluido: una comprobación que no se pudo hacer no autoriza a intentar.
        WingetService service = Build(FakeConnectivityCheck.Offline(status));

        IReadOnlyList<WingetResult> results = await service.InstallAsync(LogPackages());

        Assert.All(results, r => Assert.Equal(WingetOutcome.NoNetwork, r.Outcome));
    }

    // ---- El resumen ---------------------------------------------------------------------------

    [Fact]
    public void ElResumenNoLlamaError_ALoQueNoSeIntento()
    {
        // «4 con error» sobre un equipo desconectado manda a revisar winget en vez del cable.
        var offline = new ConnectivityResult(ConnectivityStatus.DnsFailed, "sin internet");

        var results = LogPackages()
            .Select(p => WingetResultParser.Offline(p.Id, offline))
            .ToList();

        string summary = WingetResultParser.Summarize(results);

        Assert.Contains("no tiene internet", summary);
        Assert.DoesNotContain("con error", summary);
    }

    [Fact]
    public void ConMezclaDeResultados_ElResumenSeparaSinInternetDeConError()
    {
        var offline = new ConnectivityResult(ConnectivityStatus.DnsFailed, "sin internet");

        var results = new List<WingetResult>
        {
            new("A", WingetOutcome.Installed, "ok", 0),
            WingetResultParser.Offline("B", offline),
            new("C", WingetOutcome.Failed, "reventó", 1),
        };

        string summary = WingetResultParser.Summarize(results);

        Assert.Contains("1 instalado(s)", summary);
        Assert.Contains("1 sin internet", summary);
        Assert.Contains("1 con error", summary);
    }
}
