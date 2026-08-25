using System.Text.Json;

namespace EasyFix.Core.Rollback;

/// <summary>Destino físico del journal. Se abstrae para poder testear sin tocar el disco.</summary>
public interface IJournalSink
{
    /// <summary>Escribe una línea y la baja a disco YA. No hay buffering.</summary>
    void WriteLine(string json);
}

/// <summary>
/// Registro append-only de lo que se le hizo al equipo.
/// </summary>
/// <remarks>
/// <para><b>Formato JSON Lines, no un objeto JSON único.</b> Es la decisión que hace que el journal
/// sobreviva a un crash: si el proceso muere a mitad de una escritura, se pierde la última línea y
/// todas las anteriores siguen siendo válidas. Con un JSON único, una escritura truncada invalida el
/// archivo entero y se pierde la posibilidad de deshacer.</para>
///
/// <para><b>La regla: escribir antes de actuar.</b> <see cref="Append"/> se llama y se confirma en
/// disco ANTES de tocar el sistema. Al revés, un crash entre el cambio y el registro deja un cambio
/// invisible para el undo — exactamente el caso que arruina el equipo de un cliente.</para>
/// </remarks>
public sealed class RunJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false, // una línea por registro: no negociable en JSONL
    };

    private readonly IJournalSink _sink;
    private readonly TimeProvider _time;
    private readonly List<JournalAction> _actions = new();

    private RunJournal(JournalHeader header, IJournalSink sink, TimeProvider time)
    {
        Header = header;
        _sink = sink;
        _time = time;
    }

    public JournalHeader Header { get; }

    public IReadOnlyList<JournalAction> Actions => _actions;

    public RunState State { get; private set; } = RunState.Running;

    /// <summary>Abre un journal nuevo y persiste el encabezado antes de devolverlo.</summary>
    public static RunJournal Start(JournalHeader header, IJournalSink sink, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(sink);

        var journal = new RunJournal(header, sink, time ?? TimeProvider.System);
        journal.Write(new JournalRecord { Kind = JournalRecord.KindHeader, Header = header });
        return journal;
    }

    /// <summary>
    /// Registra una acción. <b>Llamar ANTES de aplicar el cambio.</b> Si esto tira excepción, el
    /// cambio no se aplica: sin registro no hay cambio.
    /// </summary>
    public void Append(JournalAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        JournalAction stamped = action.AtUtc == default
            ? action with { AtUtc = _time.GetUtcNow() }
            : action;

        Write(new JournalRecord { Kind = JournalRecord.KindAction, Action = stamped });
        _actions.Add(stamped);
    }

    /// <summary>Marca el estado de la corrida; con <see cref="RunState.AwaitingReboot"/>, qué quedó pendiente.</summary>
    public void SetState(RunState state, IReadOnlyList<string>? pendingFixIds = null)
    {
        State = state;
        Write(new JournalRecord
        {
            Kind = JournalRecord.KindState,
            State = state,
            PendingFixIds = pendingFixIds,
        });
    }

    private void Write(JournalRecord record) =>
        _sink.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
}
