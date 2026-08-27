using EasyFix.Core.Diagnostics;
using EasyFix.Core.Rollback;

namespace EasyFix.Core.Fixes;

/// <summary>A qué botón pertenece un fix.</summary>
/// <remarks>
/// «Mejorar rendimiento» y «Reparar errores» son cosas distintas y el técnico las elige por separado:
/// mejorar tarda minutos y optimiza; reparar puede tardar una hora y deshace deterioro. Meterlos en un
/// solo botón obligaría a esperar DISM para limpiar temporales.
/// </remarks>
public enum FixCategory
{
    /// <summary>Limpieza, programas de inicio, plan de energía. Minutos.</summary>
    Performance,

    /// <summary>DISM, sfc, chkdsk, red, Windows Update. Hasta una hora.</summary>
    Repair,
}

public enum FixTier
{
    /// <summary>Se aplica solo, con el click de «Reparar». Seguro y reversible, o sin efecto lateral.</summary>
    SafeAuto,

    /// <summary>Requiere casilla marcada. Puede romper algo o el usuario pierde una función.</summary>
    RequiresApproval,
}

/// <summary>Por qué un fix no se puede aplicar en este equipo, ahora.</summary>
public enum FixBlockReason
{
    None,

    /// <summary>No hay nada que arreglar: el problema que este fix resuelve no está presente.</summary>
    NotNeeded,

    /// <summary>El disco está fallando. Escribir acelera la pérdida de datos.</summary>
    DiskFailing,

    /// <summary>BitLocker activo y el técnico no confirmó tener la clave de recuperación.</summary>
    BitLockerUnconfirmed,

    /// <summary>Equipo en dominio: las políticas lo revertirían, o rompería la gestión corporativa.</summary>
    DomainManaged,

    /// <summary>Falta un dato para decidir. Se falla cerrado.</summary>
    Undetermined,

    /// <summary>El equipo no tiene conexión y el fix la necesita.</summary>
    NoNetwork,
}

/// <param name="CanApply">Si se puede aplicar.</param>
/// <param name="Reason">Motivo cuando no.</param>
/// <param name="Explanation">Texto en español para mostrar al lado del fix.</param>
public sealed record FixApplicability(bool CanApply, FixBlockReason Reason, string Explanation)
{
    public static FixApplicability Yes(string explanation = "") =>
        new(true, FixBlockReason.None, explanation);

    public static FixApplicability No(FixBlockReason reason, string explanation) =>
        new(false, reason, explanation);
}

public enum FixStatus
{
    Applied,

    /// <summary>Corrió y no hizo falta cambiar nada. No es un fallo.</summary>
    NothingToDo,

    /// <summary>Aplicado pero necesita reinicio para tener efecto.</summary>
    AppliedNeedsReboot,

    Skipped,
    Failed,
}

/// <param name="Status">Cómo salió.</param>
/// <param name="Summary">Qué pasó, en español, para el reporte.</param>
/// <param name="FreedBytes">Bytes liberados, si aplica.</param>
public sealed record FixOutcome(FixStatus Status, string Summary, long? FreedBytes = null)
{
    public static FixOutcome Applied(string summary, long? freedBytes = null) =>
        new(FixStatus.Applied, summary, freedBytes);

    public static FixOutcome NeedsReboot(string summary) =>
        new(FixStatus.AppliedNeedsReboot, summary);

    public static FixOutcome NothingToDo(string summary) =>
        new(FixStatus.NothingToDo, summary);

    public static FixOutcome Failed(string summary) =>
        new(FixStatus.Failed, summary);

    public bool Succeeded => Status is FixStatus.Applied or FixStatus.NothingToDo or FixStatus.AppliedNeedsReboot;
}

/// <summary>
/// Lo que un fix necesita saber, y dónde registra lo que hace.
/// </summary>
/// <param name="Snapshot">Lo medido del equipo.</param>
/// <param name="Crash">Análisis de pantallazos, si hubo.</param>
/// <param name="Journal">Dónde registrar cada cambio <b>antes</b> de aplicarlo.</param>
/// <param name="BitLockerKeyConfirmed">El técnico confirmó tener la clave de recuperación a mano.</param>
public sealed record FixContext(
    SystemSnapshot Snapshot,
    CrashAnalysis? Crash,
    RunJournal Journal,
    bool BitLockerKeyConfirmed);

/// <summary>
/// Una reparación.
/// </summary>
/// <remarks>
/// <para><b>Regla dura:</b> <see cref="ApplyAsync"/> escribe en el journal <i>antes</i> de tocar el
/// sistema, nunca después. Si el proceso muere entre el cambio y el registro, queda un cambio
/// invisible para el undo — exactamente el caso que arruina el equipo de un cliente.</para>
///
/// <para><b>Un fix no decide si corresponde aplicarlo.</b> Eso lo decide <see cref="FixRunner"/>
/// consultando <see cref="CanApplyAsync"/> y las compuertas globales de seguridad. Así ningún fix
/// puede saltearse una compuerta por descuido.</para>
/// </remarks>
public interface IFix
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>A qué botón pertenece.</summary>
    FixCategory Category { get; }

    /// <summary>Qué pasa si se aplica, en español, para mostrárselo al técnico antes.</summary>
    string Description { get; }

    FixTier Tier { get; }

    /// <summary>Necesita reinicio para tener efecto.</summary>
    bool RequiresReboot { get; }

    /// <summary>
    /// Puede alterar la medición de integridad del TPM y disparar el pedido de clave de BitLocker.
    /// Todo lo que toque el arranque, el sistema de archivos o la configuración de red va en <c>true</c>.
    /// </summary>
    bool TouchesBootOrDisk { get; }

    /// <summary>Se puede deshacer. <c>false</c> se refleja tal cual en el reporte.</summary>
    bool IsReversible { get; }

    /// <summary>Tarda minutos y necesita barra de progreso.</summary>
    bool IsLongRunning { get; }

    Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct);

    Task<FixOutcome> ApplyAsync(FixContext context, IProgress<string> log, CancellationToken ct);
}
