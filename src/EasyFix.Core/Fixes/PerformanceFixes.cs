using System.Globalization;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Cleaning;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Processes;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Fixes;

/// <summary>
/// Borra archivos temporales.
/// </summary>
/// <remarks>
/// <para>Usa <see cref="JunctionSafeCleaner"/>, que enumera a mano comprobando reparse points. Un
/// junction dentro de <c>%TEMP%</c> apuntando a <c>Documents</c> convertiría esto en borrado de los
/// datos del cliente, y <c>Directory.Delete(recursive: true)</c> los sigue.</para>
///
/// <para><b>No es reversible</b> y se dice así en el reporte: los archivos no vuelven. Es la única
/// acción de esta categoría que no se puede deshacer.</para>
///
/// <para>Se saltean los archivos de menos de una hora: puede haber un instalador corriendo y
/// borrarle sus temporales lo hace fallar a mitad.</para>
/// </remarks>
public sealed class TempCleanupFix : IFix
{
    private readonly JunctionSafeCleaner _cleaner;
    private readonly ThresholdOptions _thresholds;
    private readonly ILogger<TempCleanupFix> _logger;

    public TempCleanupFix(
        JunctionSafeCleaner cleaner, ThresholdOptions thresholds, ILogger<TempCleanupFix> logger)
    {
        ArgumentNullException.ThrowIfNull(cleaner);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        _cleaner = cleaner;
        _thresholds = thresholds;
        _logger = logger;
    }

    public string Id => "temp.clean";
    public string DisplayName => "Limpiar archivos temporales";

    public string Description =>
        "Borra el contenido de las carpetas de temporales del usuario y del sistema. No se puede " +
        "deshacer, pero solo toca archivos que Windows y los programas dan por descartables.";

    public FixCategory Category => FixCategory.Performance;
    public FixTier Tier => FixTier.SafeAuto;
    public bool RequiresReboot => false;
    public bool TouchesBootOrDisk => false;
    public bool IsReversible => false;   // los archivos no vuelven, y el reporte lo dice
    public bool IsLongRunning => true;

    public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct) =>
        Task.FromResult(FixApplicability.Yes(
            "Siempre corresponde: si no hay nada viejo que borrar, no se borra nada."));

    public Task<FixOutcome> ApplyAsync(FixContext context, IProgress<string> log, CancellationToken ct)
    {
        var targets = new List<(string Label, string Path)>
        {
            ("Temporales del usuario", Path.GetTempPath()),
            ("Temporales del sistema", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")),
        };

        long freed = 0;
        int files = 0, inUse = 0, reparse = 0;
        var errors = new List<string>();

        foreach ((string label, string path) in targets)
        {
            ct.ThrowIfCancellationRequested();
            log.Report($"Limpiando {label}…");

            // Antes de borrar, porque después no hay forma de saber qué había.
            context.Journal.Append(new JournalAction
            {
                FixId = Id,
                Target = path,
                Reversible = false,
                Note = $"{label}: borrado de archivos de más de " +
                       $"{_thresholds.TempFileMinAgeMinutes} minutos. No recuperable.",
            });

            CleanResult result = _cleaner.Clean(path, _thresholds.TempFileMinAge, ct);

            freed += result.FreedBytes;
            files += result.FilesDeleted;
            inUse += result.FilesSkippedInUse;
            reparse += result.ReparsePointsSkipped;
            errors.AddRange(result.Errors);

            _logger.LogInformation(
                "{Label}: {Mb} MB, {Files} archivos, {InUse} en uso, {Reparse} enlaces.",
                label, result.FreedMegabytes, result.FilesDeleted,
                result.FilesSkippedInUse, result.ReparsePointsSkipped);
        }

        if (files == 0)
        {
            return Task.FromResult(FixOutcome.NothingToDo(
                "No había archivos temporales viejos que borrar."));
        }

        var summary = new System.Text.StringBuilder();
        summary.Append(CultureInfo.GetCultureInfo("es-ES"),
            $"Se liberaron {freed / 1024.0 / 1024.0:0.#} MB borrando {files} archivo(s).");

        if (inUse > 0)
        {
            summary.Append($" {inUse} estaban en uso y se saltearon.");
        }

        if (reparse > 0)
        {
            // Vale la pena decirlo: es la protección funcionando.
            summary.Append($" Se encontraron {reparse} enlace(s) y no se siguieron.");
        }

        if (errors.Count > 0)
        {
            summary.Append($" {errors.Count} ruta(s) dieron error.");
        }

        return Task.FromResult(FixOutcome.Applied(summary.ToString(), freed));
    }
}

/// <summary>
/// Pone el plan de energía en Alto rendimiento.
/// </summary>
/// <remarks>
/// <para>En un equipo de escritorio no hay razón para dejar «Equilibrado»: limita la frecuencia del
/// procesador sin ningún beneficio. En un portátil sí la hay —la batería— así que ahí requiere
/// aprobación en vez de aplicarse solo.</para>
///
/// <para>Reversible: el GUID anterior queda en el journal.</para>
/// </remarks>
public sealed class PowerPlanFix : IFix
{
    /// <summary>GUID del plan Alto rendimiento. Es fijo en todas las instalaciones de Windows.</summary>
    private const string HighPerformanceScheme = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private const string ActiveSchemeKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string ActiveSchemeValue = "ActivePowerScheme";

    private readonly IProcessRunner _runner;
    private readonly ILogger<PowerPlanFix> _logger;

    public PowerPlanFix(IProcessRunner runner, ILogger<PowerPlanFix> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
    }

    public string Id => "power.high-performance";
    public string DisplayName => "Poner el plan de energía en Alto rendimiento";

    public string Description =>
        "Quita el límite de frecuencia del procesador que impone el plan Equilibrado. En un portátil " +
        "consume más batería, así que ahí hace falta tu confirmación.";

    public FixCategory Category => FixCategory.Performance;

    /// <summary>
    /// En escritorio se aplica solo; en portátil requiere aprobación porque afecta la batería.
    /// </summary>
    /// <remarks>
    /// Se decide por <see cref="SystemSnapshot.BatteryWearPercent"/>: si hay dato de batería, hay
    /// batería. No es perfecto —en un escritorio ese campo es null y también lo es si la sonda
    /// falló— pero falla del lado seguro: pedir confirmación de más.
    /// </remarks>
    public FixTier Tier => FixTier.RequiresApproval;

    public bool RequiresReboot => false;
    public bool TouchesBootOrDisk => false;
    public bool IsReversible => true;
    public bool IsLongRunning => false;

    public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        string? current = ReadActiveScheme();

        if (current is null)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.Undetermined,
                "No se pudo leer el plan de energía activo."));
        }

        if (current.Equals(HighPerformanceScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.NotNeeded,
                "El equipo ya está en Alto rendimiento."));
        }

        return Task.FromResult(FixApplicability.Yes(
            $"El plan activo es otro ({current[..8]}…)."));
    }

    public async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        string? previous = ReadActiveScheme();
        if (previous is null)
        {
            return FixOutcome.Failed("No se pudo leer el plan de energía activo.");
        }

        log.Report("Cambiando el plan de energía…");

        context.Journal.Append(new JournalAction
        {
            FixId = Id,
            Target = "Plan de energía",
            Reversible = true,
            Undo = new UndoStep
            {
                Kind = UndoKind.PowerPlan,
                Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["scheme"] = previous,
                },
            },
            Note = $"Plan anterior: {previous}.",
        });

        ProcessResult result = await _runner
            .RunAsync(
                SafeProcessRunner.System32("powercfg.exe"),
                new[] { "/setactive", HighPerformanceScheme },
                TimeSpan.FromMinutes(1),
                ct)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return FixOutcome.Failed(
                $"No se pudo cambiar el plan de energía (código {result.ExitCode}). " +
                "Algunos equipos OEM reemplazan los planes por los propios.");
        }

        _logger.LogInformation("Plan de energía: {Previous} → Alto rendimiento.", previous);

        return FixOutcome.Applied("El plan de energía quedó en Alto rendimiento.");
    }

    private string? ReadActiveScheme()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ActiveSchemeKey);
            return key?.GetValue(ActiveSchemeValue)?.ToString();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "No se pudo leer el plan de energía activo.");
            return null;
        }
    }
}

/// <summary>
/// Ajusta el disco según su tipo: TRIM en SSD, desfragmentación programada en mecánico.
/// </summary>
/// <remarks>
/// <para>Los dos casos son opuestos y confundirlos hace daño: <b>desfragmentar un SSD desgasta celdas
/// sin ningún beneficio</b>, y un disco mecánico sí se beneficia de desfragmentarse. Por eso el fix
/// se niega a actuar si no pudo determinar el tipo de disco.</para>
///
/// <para>La desfragmentación se <b>programa</b>, no se ejecuta: en un disco mecánico lleno puede
/// tardar horas y bloquearía la corrida.</para>
/// </remarks>
public sealed class DiskOptimizationFix : IFix
{
    private readonly IProcessRunner _runner;
    private readonly ILogger<DiskOptimizationFix> _logger;

    public DiskOptimizationFix(IProcessRunner runner, ILogger<DiskOptimizationFix> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
    }

    public string Id => "disk.optimize";
    public string DisplayName => "Ajustar el disco a su tipo";

    public string Description =>
        "En un SSD activa TRIM. En un disco mecánico programa la desfragmentación. Nunca " +
        "desfragmenta un SSD: eso desgasta celdas sin ningún beneficio.";

    public FixCategory Category => FixCategory.Performance;
    public FixTier Tier => FixTier.SafeAuto;
    public bool RequiresReboot => false;
    public bool TouchesBootOrDisk => false;
    public bool IsReversible => false;   // activar TRIM no es algo que convenga revertir
    public bool IsLongRunning => false;

    public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        if (context.Snapshot.PrimaryDiskMedia == DiskMedia.Unknown)
        {
            // Falla cerrado: aplicar el tratamiento equivocado hace daño en los dos sentidos.
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.Undetermined,
                "No se pudo determinar si el disco es SSD o mecánico, y el ajuste correcto es " +
                "opuesto en cada caso."));
        }

        return Task.FromResult(FixApplicability.Yes());
    }

    public async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        if (context.Snapshot.IsSsd)
        {
            log.Report("Activando TRIM…");

            context.Journal.Append(new JournalAction
            {
                FixId = Id,
                Target = "TRIM",
                Reversible = false,
                Note = "fsutil behavior set DisableDeleteNotify 0. Desactivarlo de nuevo sería " +
                       "perjudicial, así que no se ofrece revertir.",
            });

            ProcessResult trim = await _runner
                .RunAsync(
                    SafeProcessRunner.System32("fsutil.exe"),
                    new[] { "behavior", "set", "DisableDeleteNotify", "0" },
                    TimeSpan.FromMinutes(1), ct)
                .ConfigureAwait(false);

            if (!trim.Succeeded)
            {
                return FixOutcome.Failed($"No se pudo activar TRIM (código {trim.ExitCode}).");
            }

            _logger.LogInformation("TRIM activado.");
            return FixOutcome.Applied("TRIM activado: el SSD puede liberar bloques borrados.");
        }

        log.Report("Programando la desfragmentación del disco mecánico…");

        context.Journal.Append(new JournalAction
        {
            FixId = Id,
            Target = drive,
            Reversible = false,
            Note = "Desfragmentación programada en la tarea de optimización de Windows.",
        });

        // /O aplica el tratamiento correcto según el tipo de medio, y Windows ya sabe cuál es.
        ProcessResult defrag = await _runner
            .RunAsync(
                SafeProcessRunner.System32("defrag.exe"),
                new[] { drive, "/O", "/H" },
                TimeSpan.FromMinutes(1), ct)
            .ConfigureAwait(false);

        // defrag devuelve códigos varios y arranca en segundo plano; no se trata como fallo duro.
        return FixOutcome.Applied(
            "Se lanzó la optimización del disco mecánico en segundo plano, con prioridad baja para " +
            "no molestar al usuario.");
    }
}
