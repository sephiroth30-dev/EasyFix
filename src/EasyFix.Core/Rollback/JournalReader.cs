using System.Text.Json;

namespace EasyFix.Core.Rollback;

/// <summary>Journal leído de disco, con lo que se pudo recuperar y lo que no.</summary>
/// <param name="Header">Encabezado. <c>null</c> si la primera línea estaba ilegible.</param>
/// <param name="Actions">Acciones en el orden en que se aplicaron.</param>
/// <param name="State">Último estado registrado.</param>
/// <param name="PendingFixIds">Fixes que quedaron esperando reinicio.</param>
/// <param name="CorruptLines">
/// Líneas que no se pudieron parsear. Casi siempre es una sola, la última, por un crash a mitad de
/// escritura. Se reporta en vez de esconderse.
/// </param>
public sealed record LoadedJournal(
    JournalHeader? Header,
    IReadOnlyList<JournalAction> Actions,
    RunState State,
    IReadOnlyList<string> PendingFixIds,
    IReadOnlyList<int> CorruptLines)
{
    public bool IsUsable => Header is not null;

    public bool HasReversibleActions => Actions.Any(a => a.Reversible && a.Undo is not null);
}

/// <summary>
/// Lee un journal <c>.jsonl</c> tolerando corrupción.
/// </summary>
/// <remarks>
/// Una línea ilegible se saltea y se anota; no invalida el archivo. Ese es el punto del formato:
/// después de un crash, poder deshacer las 14 acciones que sí quedaron registradas en vez de
/// ninguna.
/// </remarks>
public static class JournalReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LoadedJournal Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        JournalHeader? header = null;
        var actions = new List<JournalAction>();
        var corrupt = new List<int>();
        RunState state = RunState.Running;
        IReadOnlyList<string> pending = Array.Empty<string>();

        int lineNumber = 0;
        foreach (string line in lines)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<JournalRecord>(line, SerializerOptions);
            }
            catch (JsonException)
            {
                // Línea truncada por un crash. Se anota y se sigue.
                corrupt.Add(lineNumber);
                continue;
            }

            if (record is null)
            {
                corrupt.Add(lineNumber);
                continue;
            }

            switch (record.Kind)
            {
                case JournalRecord.KindHeader when record.Header is not null:
                    header = record.Header;
                    break;

                case JournalRecord.KindAction when record.Action is not null:
                    actions.Add(record.Action);
                    break;

                case JournalRecord.KindState when record.State is not null:
                    state = record.State.Value;
                    pending = record.PendingFixIds ?? Array.Empty<string>();
                    break;

                default:
                    corrupt.Add(lineNumber);
                    break;
            }
        }

        return new LoadedJournal(header, actions, state, pending, corrupt);
    }
}
