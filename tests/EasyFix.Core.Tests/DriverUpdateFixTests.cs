using EasyFix.Core.Diagnostics;
using EasyFix.Core.Drivers;
using EasyFix.Core.Fixes;
using EasyFix.Core.Network;
using EasyFix.Core.Rollback;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// <see cref="DriverUpdateFix"/> es la parte testeable del feature de drivers: toda la lógica de
/// qué instalar, qué informar y qué nunca tocar es independiente de COM/Windows Update real —
/// eso vive en <see cref="EasyFix.Core.Drivers.WindowsUpdateDriverCatalog"/>, detrás de
/// <see cref="EasyFix.Core.Drivers.IDriverCatalog"/>, y por eso se puede doblar acá.
/// </summary>
public sealed class DriverUpdateFixTests
{
    private static readonly SystemSnapshot Online = new() { Network = ConnectivityStatus.Online };

    private static DriverUpdateFix Build(FakeDriverCatalog catalog) =>
        new(catalog, NullLogger<DriverUpdateFix>.Instance);

    private static FixContext Context(SystemSnapshot? snapshot = null) =>
        new(snapshot ?? Online, Crash: null, Journal: TestJournal(), BitLockerKeyConfirmed: false);

    private static RunJournal TestJournal() =>
        RunJournal.Start(new JournalHeader { RunId = "test" }, new ListJournalSink());

    private static DriverUpdate Safe(string title = "Intel Corporation - Net - 1.0") =>
        new(Guid.NewGuid().ToString(), title, null, DriverRiskClass.Safe);

    private static DriverUpdate NeedsConfirmation(string title = "LSI - SCSIAdapter - 1.0") =>
        new(Guid.NewGuid().ToString(), title, null, DriverRiskClass.NeedsConfirmation);

    private static DriverUpdate Firmware(string title = "Dell Inc. - Firmware - 1.0") =>
        new(Guid.NewGuid().ToString(), title, null, DriverRiskClass.Firmware);

    private static readonly IProgress<string> NoopLog = new Progress<string>(_ => { });

    // ---- Compuerta de red: primer uso real de FixBlockReason.NoNetwork ----------------------

    [Theory]
    [InlineData(ConnectivityStatus.DnsFailed)]
    [InlineData(ConnectivityStatus.NoAdapter)]
    [InlineData(ConnectivityStatus.Unreachable)]
    public async Task SinInternet_SeBloqueaConNoNetwork(ConnectivityStatus offline)
    {
        FixApplicability result = await Build(FakeDriverCatalog.Empty())
            .CanApplyAsync(Context(new SystemSnapshot { Network = offline }), CancellationToken.None);

        Assert.False(result.CanApply);
        Assert.Equal(FixBlockReason.NoNetwork, result.Reason);
    }

    [Fact]
    public async Task RedSinComprobar_FallaCerrado_SeBloqueaIgualQueSinInternet()
    {
        // null = no medido. Mismo principio que el resto del proyecto: fallar cerrado.
        FixApplicability result = await Build(FakeDriverCatalog.Empty())
            .CanApplyAsync(Context(new SystemSnapshot { Network = null }), CancellationToken.None);

        Assert.False(result.CanApply);
        Assert.Equal(FixBlockReason.NoNetwork, result.Reason);
    }

    [Fact]
    public async Task ConInternet_SePermite()
    {
        FixApplicability result = await Build(FakeDriverCatalog.Empty())
            .CanApplyAsync(Context(), CancellationToken.None);

        Assert.True(result.CanApply);
    }

    // ---- Qué se instala y qué no -------------------------------------------------------------

    [Fact]
    public async Task SoloSafe_SeInstala_NeedsConfirmationYFirmwareNuncaLlegaAInstallAsync()
    {
        DriverUpdate safe = Safe();
        DriverUpdate confirm = NeedsConfirmation();
        DriverUpdate fw = Firmware();

        var catalog = FakeDriverCatalog.WithUpdates(safe, confirm, fw);

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        // El catálogo fake registra qué UpdateIds le pidieron instalar: tienen que ser
        // EXACTAMENTE los Safe, ni uno más.
        Assert.Equal(new[] { safe.UpdateId }, catalog.InstalledUpdateIds);
        Assert.Equal(FixStatus.Applied, outcome.Status);
    }

    [Fact]
    public async Task SinNadaParaInstalar_NothingToDo_NoLlamaAInstallAsync()
    {
        var catalog = FakeDriverCatalog.Empty();

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.NothingToDo, outcome.Status);
        Assert.Empty(catalog.InstalledUpdateIds);
    }

    [Fact]
    public async Task SoloNeedsConfirmation_NothingToDo_PeroElResumenLosNombra()
    {
        DriverUpdate confirm = NeedsConfirmation("LSI - SCSIAdapter - 9.9");
        var catalog = FakeDriverCatalog.WithUpdates(confirm);

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.NothingToDo, outcome.Status);
        Assert.Empty(catalog.InstalledUpdateIds); // nunca se instaló
        Assert.Contains("LSI - SCSIAdapter - 9.9", outcome.Summary, StringComparison.Ordinal);

        // El contrato es que el resumen diga que NO se tocaron. Antes esto exigía la palabra
        // «confirmación», que era la redacción de entonces: al acortar los textos por pedido del
        // técnico («no quiero ese tipo de explicaciones»), la prueba falló aunque el
        // comportamiento seguía intacto.
        //
        // Una prueba que fija la redacción exacta en vez de la conducta se rompe con cada ajuste
        // de copy y no detecta ningún defecto real — así que se corrigió la ASERCIÓN, no el texto
        // del producto.
        Assert.Contains("sin tocar", outcome.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SoloFirmware_NothingToDo_YElResumenAdvierteQueNuncaSeToca()
    {
        DriverUpdate fw = Firmware("Lenovo - BIOS - 2.1");
        var catalog = FakeDriverCatalog.WithUpdates(fw);

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.NothingToDo, outcome.Status);
        Assert.Empty(catalog.InstalledUpdateIds);
        Assert.Contains("Lenovo - BIOS - 2.1", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("fabricante", outcome.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Resultado de la instalación ----------------------------------------------------------

    [Fact]
    public async Task InstalacionExitosaConReinicio_AppliedNeedsReboot()
    {
        var catalog = new FakeDriverCatalog(
            DriverSearchResult.Ok(new[] { Safe() }),
            install: _ => new DriverInstallResult(true, RebootRequired: true, "ok"));

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.AppliedNeedsReboot, outcome.Status);
    }

    [Fact]
    public async Task InstalacionExitosaSinReinicio_Applied()
    {
        var catalog = new FakeDriverCatalog(
            DriverSearchResult.Ok(new[] { Safe() }),
            install: _ => new DriverInstallResult(true, RebootRequired: false, "ok"));

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.Applied, outcome.Status);
    }

    [Fact]
    public async Task InstalacionFalla_Failed_NoSeAfirmaExito()
    {
        var catalog = new FakeDriverCatalog(
            DriverSearchResult.Ok(new[] { Safe() }),
            install: _ => new DriverInstallResult(false, false, "La descarga no terminó bien."));

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.Failed, outcome.Status);
        Assert.Contains("no terminó bien", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusquedaFalla_Failed_NuncaDiceCeroActualizaciones()
    {
        // La diferencia que importa: "falló la búsqueda" no puede sonar igual que "no hay nada".
        var catalog = FakeDriverCatalog.SearchFails("El servicio de Windows Update está detenido.");

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.Failed, outcome.Status);
        Assert.Contains("El servicio de Windows Update está detenido.", outcome.Summary, StringComparison.Ordinal);
        Assert.Empty(catalog.InstalledUpdateIds);
    }

    [Fact]
    public async Task MezclaDeLasTresCategorias_ElResumenLasMencionaATodas()
    {
        DriverUpdate safe = Safe("Realtek - Media - 1.0");
        DriverUpdate confirm = NeedsConfirmation("Samsung - Storage - 1.0");
        DriverUpdate fw = Firmware("HP - UEFI - 1.0");

        var catalog = new FakeDriverCatalog(
            DriverSearchResult.Ok(new[] { safe, confirm, fw }),
            install: _ => new DriverInstallResult(true, false, "ok"));

        FixOutcome outcome = await Build(catalog).ApplyAsync(Context(), NoopLog, CancellationToken.None);

        Assert.Equal(FixStatus.Applied, outcome.Status);
        Assert.Contains("Realtek - Media - 1.0", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("Samsung - Storage - 1.0", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains("HP - UEFI - 1.0", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Declaraciones_CoherentesConElDiseno()
    {
        var fix = Build(FakeDriverCatalog.Empty());

        Assert.Equal(FixCategory.Repair, fix.Category);
        Assert.True(fix.RequiresReboot);
        Assert.False(fix.TouchesBootOrDisk); // Safe excluye almacenamiento/firmware por diseño
        Assert.False(fix.IsReversible);       // depende del punto de restauración, no de un undo propio
    }
}
