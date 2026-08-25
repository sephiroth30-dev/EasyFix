using EasyFix.Core.Rollback;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>Reloj fijo. .NET 8 no trae un TimeProvider de test sin agregar otro paquete.</summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Journal en memoria que además puede simular el crash: <see cref="TruncateLastLine"/> corta la
/// última línea a la mitad, igual que un proceso que muere durante la escritura.
/// </summary>
public sealed class ListJournalSink : IJournalSink
{
    public List<string> Lines { get; } = new();

    public void WriteLine(string json) => Lines.Add(json);

    public void TruncateLastLine(int keepChars = 20)
    {
        if (Lines.Count == 0)
        {
            return;
        }

        string last = Lines[^1];
        Lines[^1] = last.Length <= keepChars ? last : last[..keepChars];
    }
}

/// <summary>Handler de deshacer configurable: registra lo que recibió y puede fallar a pedido.</summary>
public sealed class RecordingUndoHandler : IUndoHandler
{
    private readonly bool _returns;
    private readonly Exception? _throws;

    public RecordingUndoHandler(UndoKind kind, bool returns = true, Exception? throws = null)
    {
        Kind = kind;
        _returns = returns;
        _throws = throws;
    }

    public UndoKind Kind { get; }

    /// <summary>Pasos recibidos, en orden de llegada.</summary>
    public List<UndoStep> Received { get; } = new();

    public Task<bool> UndoAsync(UndoStep step, CancellationToken ct)
    {
        Received.Add(step);

        if (_throws is not null)
        {
            throw _throws;
        }

        return Task.FromResult(_returns);
    }
}
