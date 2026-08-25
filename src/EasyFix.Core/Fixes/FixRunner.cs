using EasyFix.Core.Diagnostics;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Fixes;

/// <summary>Crea el punto de restauración. Lo implementa la capa que habla con Windows.</summary>
public interface IRestorePointService
{
    /// <summary>
    /// Crea un punto de restauración y <b>verifica que exista</b>. Devuelve su número de secuencia,
    /// o <c>null</c> si no se pudo crear.
    /// </summary>
    Task<long?> CreateAsync(string description, CancellationToken ct);
}

/// <summary>Suspende BitLocker antes de los fixes que tocan arranque o disco.</summary>
public interface IBitLockerService
{
    /// <summary>Suspende la protección hasta el próximo reinicio. <c>false</c> si no se pudo.</summary>
    Task<bool> SuspendUntilNextRebootAsync(CancellationToken ct);
}

/// <param name="FixId">Cuál.</param>
/// <param name="DisplayName">Nombre para mostrar.</param>
/// <param name="Outcome">Cómo salió, si se ejecutó.</param>
/// <param name="Blocked">Por qué no se ejecutó, si no se ejecutó.</param>
public sealed record FixReport(
    string FixId,
    string DisplayName,
    FixOutcome? Outcome,
    FixApplicability? Blocked);

/// <param name="Aborted">La corrida se abortó antes de tocar nada.</param>
/// <param name="AbortReason">Por qué.</param>
/// <param name="RestorePointSequence">Secuencia del punto de restauración creado.</param>
/// <param name="Results">Un resultado por fix considerado.</param>
/// <param name="RebootRequired">Quedaron cambios que necesitan reinicio.</param>
public sealed record RunResult(
    bool Aborted,
    string? AbortReason,
    long? RestorePointSequence,
    IReadOnlyList<FixReport> Results,
    bool RebootRequired)
{
    public IEnumerable<FixReport> Applied =>
        Results.Where(r => r.Outcome?.Status is FixStatus.Applied or FixStatus.AppliedNeedsReboot);

    public IEnumerable<FixReport> Failed =>
        Results.Where(r => r.Outcome?.Status == FixStatus.Failed);

    public IEnumerable<FixReport> BlockedFixes =>
        Results.Where(r => r.Blocked is not null);

    public long TotalFreedBytes => Results.Sum(r => r.Outcome?.FreedBytes ?? 0);
}

/// <summary>
/// Aplica los fixes con las compuertas de seguridad puestas.
/// </summary>
/// <remarks>
/// <para>Toda la política de seguridad vive acá y en un solo lugar, para que ningún fix pueda
/// saltearse una compuerta por descuido. El orden no es negociable:</para>
///
/// <list type="number">
/// <item><b>Disco fallando → abortar.</b> Antes de cualquier otra cosa. Escribir en un disco que se
/// está muriendo acelera la pérdida de datos, y "optimizarlo" es la peor jugada posible.</item>
/// <item><b>Punto de restauración verificado → si no, abortar.</b> Sin red de seguridad no se toca
/// nada. No alcanza con pedirlo: hay que confirmar que existe.</item>
/// <item><b>BitLocker sin confirmar → se bloquean los fixes que tocan arranque o disco</b>, y el
/// resto corre normal. Si está confirmado, se suspende la protección antes.</item>
/// <item><b>Equipo en dominio → modo restringido.</b> Las políticas revierten los cambios y romper
/// la gestión corporativa deja al equipo inservible para el área de sistemas.</item>
/// <item>Recién ahí, cada fix decide si le corresponde.</item>
/// </list>
///
/// <para>Sin dependencias de Windows: los servicios entran por interfaz, así que toda esta política
/// se testea con dobles.</para>
/// </remarks>
public sealed class FixRunner
{
    private readonly IRestorePointService _restorePoints;
    private readonly IBitLockerService _bitLocker;
    private readonly ILogger<FixRunner> _logger;

    public FixRunner(
        IRestorePointService restorePoints,
        IBitLockerService bitLocker,
        ILogger<FixRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(restorePoints);
        ArgumentNullException.ThrowIfNull(bitLocker);
        ArgumentNullException.ThrowIfNull(logger);

        _restorePoints = restorePoints;
        _bitLocker = bitLocker;
        _logger = logger;
    }

    /// <summary>
    /// Aplica los fixes indicados. No tira excepción por un fix que falla: devuelve un reporte por
    /// cada uno.
    /// </summary>
    /// <param name="fixes">Los fixes a considerar, en el orden en que se van a intentar.</param>
    /// <param name="snapshot">Lo medido del equipo.</param>
    /// <param name="crash">Análisis de pantallazos, si hubo.</param>
    /// <param name="journal">Journal ya abierto. Cada cambio se registra antes de aplicarse.</param>
    /// <param name="bitLockerKeyConfirmed">El técnico confirmó tener la clave de recuperación.</param>
    /// <param name="log">Progreso línea por línea.</param>
    public async Task<RunResult> RunAsync(
        IReadOnlyList<IFix> fixes,
        SystemSnapshot snapshot,
        CrashAnalysis? crash,
        RunJournal journal,
        bool bitLockerKeyConfirmed,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(journal);

        var progress = log ?? new Progress<string>();

        // ---- Compuerta 1: disco fallando ---------------------------------------------------
        if (snapshot.PrimaryDiskHealth == DiskHealth.Failing)
        {
            const string Reason =
                "El disco está fallando (SMART predice falla). No se aplicó ningún cambio: escribir " +
                "en un disco que se está muriendo acelera la pérdida de datos. Respaldá y reemplazá " +
                "el disco.";

            _logger.LogWarning("Corrida abortada: disco fallando.");
            journal.SetState(RunState.Aborted);
            return Abort(Reason);
        }

        // ---- Compuerta 2: punto de restauración --------------------------------------------
        progress.Report("Creando punto de restauración…");

        long? sequence;
        try
        {
            sequence = await _restorePoints
                .CreateAsync($"EasyFix {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}", ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo crear el punto de restauración.");
            sequence = null;
        }

        if (sequence is null)
        {
            const string Reason =
                "No se pudo crear un punto de restauración, así que no se aplicó ningún cambio. " +
                "Suele ser porque Restaurar sistema está deshabilitado, o porque no hay espacio " +
                "libre en C:. Habilitalo y volvé a intentar.";

            _logger.LogWarning("Corrida abortada: sin punto de restauración.");
            journal.SetState(RunState.Aborted);
            return Abort(Reason);
        }

        progress.Report($"Punto de restauración creado (secuencia {sequence}).");

        // ---- Compuerta 3: BitLocker ---------------------------------------------------------
        bool bootFixesAllowed = true;

        if (snapshot.BitLockerActive)
        {
            if (!bitLockerKeyConfirmed)
            {
                bootFixesAllowed = false;
                progress.Report(
                    "BitLocker activo sin confirmar la clave: se saltean las reparaciones que tocan " +
                    "arranque o disco.");
            }
            else
            {
                progress.Report("Suspendiendo BitLocker hasta el próximo reinicio…");

                bool suspended = await _bitLocker.SuspendUntilNextRebootAsync(ct).ConfigureAwait(false);
                if (!suspended)
                {
                    // No se pudo suspender: se bloquean esos fixes en vez de arriesgarse a que el
                    // cliente quede fuera de su equipo.
                    bootFixesAllowed = false;
                    progress.Report(
                        "No se pudo suspender BitLocker: se saltean las reparaciones que tocan " +
                        "arranque o disco.");
                }
                else
                {
                    journal.Append(new JournalAction
                    {
                        FixId = "bitlocker.suspend",
                        Target = "C:",
                        Reversible = true,
                        Undo = new UndoStep { Kind = UndoKind.BitLockerResume },
                        Note = "Suspendido hasta el próximo reinicio.",
                    });
                }
            }
        }

        // ---- Aplicar ------------------------------------------------------------------------
        var results = new List<FixReport>();
        var pending = new List<string>();

        foreach (IFix fix in fixes)
        {
            ct.ThrowIfCancellationRequested();

            FixApplicability? gate = EvaluateGates(fix, snapshot, bootFixesAllowed);
            if (gate is not null)
            {
                _logger.LogInformation(
                    "{FixId} bloqueado: {Reason}.", fix.Id, gate.Reason);
                results.Add(new FixReport(fix.Id, fix.DisplayName, null, gate));
                continue;
            }

            var context = new FixContext(snapshot, crash, journal, bitLockerKeyConfirmed);

            FixApplicability applicability;
            try
            {
                applicability = await fix.CanApplyAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{FixId}: falló la comprobación previa.", fix.Id);
                results.Add(new FixReport(fix.Id, fix.DisplayName, null,
                    FixApplicability.No(FixBlockReason.Undetermined,
                        $"No se pudo comprobar si corresponde: {ex.Message}")));
                continue;
            }

            if (!applicability.CanApply)
            {
                results.Add(new FixReport(fix.Id, fix.DisplayName, null, applicability));
                continue;
            }

            progress.Report(fix.DisplayName);

            try
            {
                FixOutcome outcome = await fix.ApplyAsync(context, progress, ct).ConfigureAwait(false);
                results.Add(new FixReport(fix.Id, fix.DisplayName, outcome, null));

                if (outcome.Status == FixStatus.AppliedNeedsReboot)
                {
                    pending.Add(fix.Id);
                }

                _logger.LogInformation("{FixId}: {Status}. {Summary}", fix.Id, outcome.Status, outcome.Summary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Un fix que revienta no corta la corrida: los demás se siguen aplicando y lo que
                // ya se hizo queda en el journal, así que sigue siendo reversible.
                _logger.LogError(ex, "{FixId} falló.", fix.Id);
                results.Add(new FixReport(fix.Id, fix.DisplayName,
                    FixOutcome.Failed($"{ex.GetType().Name}: {ex.Message}"), null));
            }
        }

        bool rebootRequired = pending.Count > 0;
        journal.SetState(
            rebootRequired ? RunState.AwaitingReboot : RunState.Completed,
            rebootRequired ? pending : null);

        return new RunResult(false, null, sequence, results, rebootRequired);

        static RunResult Abort(string reason) =>
            new(true, reason, null, Array.Empty<FixReport>(), false);
    }

    /// <summary>
    /// Compuertas globales. Devuelve <c>null</c> cuando el fix pasa todas.
    /// </summary>
    private static FixApplicability? EvaluateGates(IFix fix, SystemSnapshot snapshot, bool bootFixesAllowed)
    {
        if (fix.TouchesBootOrDisk && !bootFixesAllowed)
        {
            return FixApplicability.No(
                FixBlockReason.BitLockerUnconfirmed,
                "Toca el arranque o el disco, y BitLocker está activo sin la clave de recuperación " +
                "confirmada. Si Windows pidiera la clave al reiniciar y el cliente no la tiene, " +
                "quedaría fuera de su propio equipo.");
        }

        // En dominio, solo pasa lo que no deja rastro en la configuración gestionada. Un fix
        // reversible que toca configuración sería revertido por la GPO igual: parece funcionar y se
        // deshace solo.
        if (snapshot.IsDomainJoined && fix.Tier == FixTier.RequiresApproval)
        {
            return FixApplicability.No(
                FixBlockReason.DomainManaged,
                "El equipo está en un dominio: las políticas de la empresa revertirían este cambio, " +
                "y desactivar componentes corporativos lo rompe para el área de sistemas.");
        }

        return null;
    }
}
