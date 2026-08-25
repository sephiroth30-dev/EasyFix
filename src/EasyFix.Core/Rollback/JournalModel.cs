using System.Text.Json.Serialization;

namespace EasyFix.Core.Rollback;

/// <summary>Tipo de paso de deshacer. Cada uno lo atiende un <see cref="IUndoHandler"/>.</summary>
public enum UndoKind
{
    /// <summary>La acción no se puede deshacer (borrado de archivos). Solo queda registrada.</summary>
    None = 0,

    /// <summary>Restaurar un valor REG_BINARY — el flag de <c>StartupApproved</c>.</summary>
    RegistryBinaryValue,

    /// <summary>Restaurar un valor de registro de tipo string/DWORD.</summary>
    RegistryValue,

    /// <summary>Borrar un valor que no existía antes de que lo creáramos.</summary>
    RegistryValueDelete,

    /// <summary>Volver al esquema de energía anterior.</summary>
    PowerPlan,

    /// <summary>Volver al tipo de inicio anterior de un servicio (y, si estaba corriendo, arrancarlo).</summary>
    ServiceStartMode,

    /// <summary>Volver a habilitar una tarea programada.</summary>
    ScheduledTask,

    /// <summary>Reanudar la protección de BitLocker que se suspendió.</summary>
    BitLockerResume,
}

/// <summary>Cómo revertir una acción. El payload es plano a propósito: se serializa sin sorpresas.</summary>
public sealed record UndoStep
{
    public UndoKind Kind { get; init; } = UndoKind.None;

    public IReadOnlyDictionary<string, string> Payload { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string Require(string key) =>
        Payload.TryGetValue(key, out string? v) && v is not null
            ? v
            : throw new InvalidOperationException($"El paso de deshacer {Kind} no trae la clave '{key}'.");
}

/// <summary>Una acción aplicada al sistema.</summary>
public sealed record JournalAction
{
    public string FixId { get; init; } = string.Empty;

    /// <summary>Sobre qué se actuó: clave de registro, nombre de servicio, ruta.</summary>
    public string Target { get; init; } = string.Empty;

    public DateTimeOffset AtUtc { get; init; }

    /// <summary>
    /// <c>false</c> para borrado de archivos. Se refleja tal cual en el reporte: no se le miente al
    /// usuario diciendo que "Deshacer todo" recupera lo borrado.
    /// </summary>
    public bool Reversible { get; init; }

    public UndoStep? Undo { get; init; }

    public long? FreedBytes { get; init; }

    public string? Note { get; init; }
}

/// <summary>Estado de la corrida. Sobrevive a un reinicio.</summary>
public enum RunState
{
    Running,

    /// <summary>
    /// Quedaron fixes pendientes que necesitan reinicio (SFC, DISM /RestoreHealth, chkdsk /f,
    /// resets de red). La app se reengancha por <c>RunOnce</c> con <c>--resume &lt;runId&gt;</c>.
    /// </summary>
    AwaitingReboot,

    Completed,
    UndoneByUser,
    Aborted,
}

/// <summary>Primera línea del journal.</summary>
public sealed record JournalHeader
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    /// Secuencia del punto de restauración creado antes del primer cambio. Si es <c>null</c>, no se
    /// pudo crear y la corrida tuvo que abortarse.
    /// </summary>
    public long? RestorePointSequence { get; init; }

    public bool BitLockerWasSuspended { get; init; }
    public bool DomainJoined { get; init; }
    public string? MachineName { get; init; }
    public string? OsVersion { get; init; }

    /// <summary>Boot time medido ANTES de aplicar nada, en ms. Es la mitad del antes/después.</summary>
    public int? BaselineMainPathBootTimeMs { get; init; }
}

/// <summary>Envoltorio de cada línea del archivo <c>.jsonl</c>.</summary>
public sealed record JournalRecord
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    public JournalHeader? Header { get; init; }
    public JournalAction? Action { get; init; }
    public RunState? State { get; init; }
    public IReadOnlyList<string>? PendingFixIds { get; init; }

    public const string KindHeader = "header";
    public const string KindAction = "action";
    public const string KindState = "state";
}
