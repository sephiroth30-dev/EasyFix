using System.Text;
using EasyFix.Core.Drivers;
using EasyFix.Core.Network;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Fixes;

/// <summary>
/// Busca e instala actualizaciones de driver, con tres niveles de seguridad según
/// <see cref="DriverRiskClass"/>.
/// </summary>
/// <remarks>
/// <para><b>Solo se instala <see cref="DriverRiskClass.Safe"/>, sin pedir nada más.</b> Es
/// <see cref="FixTier.SafeAuto"/> a propósito: el filtro de riesgo ya lo hizo
/// <see cref="DriverRiskClassifier"/> antes de llegar acá — no es "instalar cualquier driver
/// solo", es "instalar solo los que ya se decidió que son de bajo impacto". Los de
/// almacenamiento (y cualquiera sin clasificar) se informan, nunca se tocan. El firmware ni
/// siquiera se ofrece: va al mismo tratamiento que un disco mecánico o RAM insuficiente —
/// nombrarlo, no intentar arreglarlo.</para>
///
/// <para><b>Primer uso real de <see cref="FixBlockReason.NoNetwork"/>.</b> El catálogo de
/// Windows Update necesita internet; el enum existía desde v0.8.0 sin ningún consumidor —
/// se decidió a propósito no bloquear DISM/SFC/chkdsk por falta de red, pero este fix sí
/// depende de ella por completo.</para>
///
/// <para><b>SIN VERIFICAR EN WINDOWS REAL — el código de mayor riesgo del proyecto.</b>
/// <see cref="WindowsUpdateDriverCatalog"/> usa COM tardío (<c>dynamic</c>): ningún build valida
/// que los miembros de la API de WUA existan de verdad. Ver la remark de esa clase.</para>
/// </remarks>
public sealed class DriverUpdateFix : IFix
{
    private readonly IDriverCatalog _catalog;
    private readonly ILogger<DriverUpdateFix> _logger;

    public DriverUpdateFix(IDriverCatalog catalog, ILogger<DriverUpdateFix> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);

        _catalog = catalog;
        _logger = logger;
    }

    public string Id => "drivers.update";
    public string DisplayName => "Actualizar controladores";

    public string Description =>
        "Busca en el catálogo oficial de Windows Update e instala los controladores de bajo " +
        "riesgo: red, video, audio, USB, Bluetooth, impresora, entrada. Los de almacenamiento " +
        "se informan sin instalar — necesitan tu confirmación manual. El firmware o BIOS nunca " +
        "se toca desde acá.";

    public FixCategory Category => FixCategory.Repair;

    // SafeAuto es correcto acá: la decisión de riesgo ya la tomó el clasificador antes de que
    // este fix exista en el reporte. No es "confiar en que cualquier driver es seguro".
    public FixTier Tier => FixTier.SafeAuto;

    public bool RequiresReboot => true;

    // La clase Safe excluye por diseño almacenamiento y firmware — lo único que de verdad podría
    // afectar el arranque o el disco. No hace falta la compuerta de BitLocker para esto.
    public bool TouchesBootOrDisk => false;

    // EasyFix no tiene un UndoHandler para esto: depende del punto de restauración, como la
    // limpieza de temporales. Decirlo tal cual en vez de prometer un "Deshacer" que no existe.
    public bool IsReversible => false;

    public bool IsLongRunning => true;

    public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Snapshot.Network != ConnectivityStatus.Online)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.NoNetwork,
                "Buscar e instalar drivers necesita internet, y el equipo no lo tiene (o no se " +
                "pudo comprobar)."));
        }

        return Task.FromResult(FixApplicability.Yes());
    }

    public async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);

        log.Report("Buscando actualizaciones de driver en el catálogo de Windows Update…");

        DriverSearchResult search = await _catalog.SearchAsync(ct).ConfigureAwait(false);

        if (!search.Succeeded)
        {
            return FixOutcome.Failed(
                $"No se pudo consultar el catálogo de drivers: {search.FailureReason}");
        }

        List<DriverUpdate> safe = search.Updates
            .Where(u => u.RiskClass == DriverRiskClass.Safe).ToList();
        List<DriverUpdate> needsConfirmation = search.Updates
            .Where(u => u.RiskClass == DriverRiskClass.NeedsConfirmation).ToList();
        List<DriverUpdate> firmware = search.Updates
            .Where(u => u.RiskClass == DriverRiskClass.Firmware).ToList();

        _logger.LogInformation(
            "Drivers encontrados: {Safe} de bajo riesgo, {Confirm} necesitan confirmación, " +
            "{Firmware} firmware/BIOS.", safe.Count, needsConfirmation.Count, firmware.Count);

        bool rebootRequired = false;
        bool anyInstallFailed = false;
        var summary = new StringBuilder();

        if (safe.Count == 0)
        {
            summary.Append("No hace falta actualizar ningún controlador.");
        }
        else
        {
            log.Report($"Instalando {safe.Count} driver(s) de bajo riesgo: " +
                       $"{string.Join(", ", safe.Select(u => u.Title))}");

            var installProgress = new Progress<string>(msg => log.Report(msg));
            DriverInstallResult install = await _catalog
                .InstallAsync(safe.Select(u => u.UpdateId).ToList(), installProgress, ct)
                .ConfigureAwait(false);

            if (install.Success)
            {
                summary.Append(
                    $"Se instalaron {safe.Count} driver(s): {string.Join(", ", safe.Select(u => u.Title))}.");
            }
            else
            {
                anyInstallFailed = true;
                summary.Append($"No se pudieron instalar los drivers de bajo riesgo: {install.Detail}");
            }

            rebootRequired = install.RebootRequired;
        }

        if (needsConfirmation.Count > 0)
        {
            // Corto: cuáles son y que no se tocaron. El porqué está documentado en el código,
            // no tiene que ocupar la pantalla del técnico.
            summary.Append(
                $" {needsConfirmation.Count} de almacenamiento quedaron sin tocar: " +
                $"{string.Join(", ", needsConfirmation.Select(u => u.Title))}.");
        }

        if (firmware.Count > 0)
        {
            summary.Append(
                $" Hay {firmware.Count} de firmware o BIOS: esas se bajan del fabricante. " +
                $"{string.Join(", ", firmware.Select(u => u.Title))}.");
        }

        if (safe.Count == 0 && needsConfirmation.Count == 0 && firmware.Count == 0)
        {
            return FixOutcome.NothingToDo(summary.ToString());
        }

        if (anyInstallFailed)
        {
            return FixOutcome.Failed(summary.ToString());
        }

        if (safe.Count == 0)
        {
            // Solo había de las otras dos categorías: no se instaló nada, pero no es un fallo.
            return FixOutcome.NothingToDo(summary.ToString());
        }

        return rebootRequired
            ? FixOutcome.NeedsReboot(summary.ToString())
            : FixOutcome.Applied(summary.ToString());
    }
}
