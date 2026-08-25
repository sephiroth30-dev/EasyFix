using EasyFix.Core.Rollback;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

public sealed class JournalTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 20, 14, 33, TimeSpan.Zero);

    private static JournalHeader Header() => new()
    {
        RunId = "2026-08-24T20-14-33",
        StartedUtc = Now,
        RestorePointSequence = 42,
        MachineName = "PC-CLIENTE",
        BaselineMainPathBootTimeMs = 94_000,
    };

    private static JournalAction StartupAction(string name) => new()
    {
        FixId = "startup.disable",
        Target = $@"HKCU\...\StartupApproved\Run\{name}",
        Reversible = true,
        Undo = new UndoStep
        {
            Kind = UndoKind.RegistryBinaryValue,
            Payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = $@"HKCU\...\StartupApproved\Run",
                ["name"] = name,
                ["valueHex"] = "020000000000000000000000",
            },
        },
    };

    [Fact]
    public void Start_PersisteElEncabezadoAntesDeDevolver()
    {
        var sink = new ListJournalSink();

        RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));

        // Una sola línea, y ya está en el sink: el encabezado no espera a la primera acción.
        Assert.Single(sink.Lines);
        Assert.Contains("\"header\"", sink.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Append_EscribeUnaLineaPorAccion()
    {
        var sink = new ListJournalSink();
        RunJournal journal = RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));

        journal.Append(StartupAction("Spotify"));
        journal.Append(StartupAction("Steam"));

        Assert.Equal(3, sink.Lines.Count); // encabezado + 2 acciones
        Assert.Equal(2, journal.Actions.Count);
    }

    [Fact]
    public void Append_SellaLaFechaConElRelojInyectado()
    {
        var sink = new ListJournalSink();
        RunJournal journal = RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));

        journal.Append(StartupAction("Spotify"));

        Assert.Equal(Now, journal.Actions[0].AtUtc);
    }

    [Fact]
    public void IdaYVuelta_ConservaLoQueHaceFaltaParaDeshacer()
    {
        var sink = new ListJournalSink();
        RunJournal journal = RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));
        journal.Append(StartupAction("Spotify"));

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);

        Assert.True(loaded.IsUsable);
        Assert.Equal(42, loaded.Header!.RestorePointSequence);
        Assert.Equal(94_000, loaded.Header.BaselineMainPathBootTimeMs);
        Assert.Single(loaded.Actions);
        Assert.Equal(UndoKind.RegistryBinaryValue, loaded.Actions[0].Undo!.Kind);
        Assert.Equal("020000000000000000000000", loaded.Actions[0].Undo!.Require("valueHex"));
        Assert.Empty(loaded.CorruptLines);
    }

    [Fact]
    public void LineaTruncadaPorUnCrash_NoInvalidaLasAnteriores()
    {
        // Este es el motivo de usar JSON Lines y no un objeto JSON único: con un solo objeto, una
        // escritura cortada a la mitad deja el archivo entero inservible y no se puede deshacer nada.
        var sink = new ListJournalSink();
        RunJournal journal = RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));
        journal.Append(StartupAction("Spotify"));
        journal.Append(StartupAction("Steam"));
        journal.Append(StartupAction("Discord"));

        sink.TruncateLastLine(); // el proceso murió escribiendo la última

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);

        Assert.True(loaded.IsUsable);
        Assert.Equal(2, loaded.Actions.Count);        // las dos primeras se recuperan
        Assert.Single(loaded.CorruptLines);           // y la pérdida se reporta, no se esconde
        Assert.Equal(4, loaded.CorruptLines[0]);
    }

    [Fact]
    public void SetState_AwaitingReboot_GuardaLosFixesPendientes()
    {
        var sink = new ListJournalSink();
        RunJournal journal = RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));

        journal.SetState(RunState.AwaitingReboot, new[] { "repair.sfc", "network.reset" });

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.Equal(RunState.AwaitingReboot, loaded.State);
        Assert.Equal(new[] { "repair.sfc", "network.reset" }, loaded.PendingFixIds);
    }

    [Fact]
    public void EncabezadoIlegible_DejaElJournalInutilizable()
    {
        LoadedJournal loaded = JournalReader.Parse(new[] { "{esto no es json", "tampoco esto" });

        Assert.False(loaded.IsUsable);
        Assert.Equal(2, loaded.CorruptLines.Count);
    }

    [Fact]
    public void LineasVacias_SeIgnoran_SinContarComoCorruptas()
    {
        var sink = new ListJournalSink();
        RunJournal.Start(Header(), sink, new FixedTimeProvider(Now));

        LoadedJournal loaded = JournalReader.Parse(new[] { sink.Lines[0], string.Empty, "   " });

        Assert.True(loaded.IsUsable);
        Assert.Empty(loaded.CorruptLines);
    }

    [Fact]
    public void Require_ConClaveAusente_TiraExcepcionClara()
    {
        var step = new UndoStep { Kind = UndoKind.PowerPlan };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => step.Require("scheme"));
        Assert.Contains("scheme", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class UndoEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);

    private static JournalAction Reversible(string fixId, UndoKind kind) => new()
    {
        FixId = fixId,
        Target = fixId,
        AtUtc = Now,
        Reversible = true,
        Undo = new UndoStep { Kind = kind },
    };

    private static JournalAction NotReversible(string fixId, long freedBytes) => new()
    {
        FixId = fixId,
        Target = fixId,
        AtUtc = Now,
        Reversible = false,
        FreedBytes = freedBytes,
    };

    private static LoadedJournal Journal(params JournalAction[] actions) =>
        new(new JournalHeader { RunId = "r1" }, actions, RunState.Completed,
            Array.Empty<string>(), Array.Empty<int>());

    private static UndoEngine Build(params IUndoHandler[] handlers) =>
        new(handlers, NullLogger<UndoEngine>.Instance);

    [Fact]
    public async Task DeshaceEnOrdenInverso()
    {
        // Los cambios pueden depender entre sí: revertir en el orden original dejaría el sistema en
        // un estado que nunca existió.
        var handler = new RecordingUndoHandler(UndoKind.RegistryValue);
        var engine = Build(handler);

        var journal = Journal(
            Reversible("primero", UndoKind.RegistryValue),
            Reversible("segundo", UndoKind.RegistryValue),
            Reversible("tercero", UndoKind.RegistryValue));

        UndoReport report = await engine.UndoAsync(journal);

        Assert.Equal(3, report.Undone.Count);
        Assert.Equal("tercero", report.Undone[0].FixId);
        Assert.Equal("primero", report.Undone[2].FixId);
        Assert.True(report.FullySucceeded);
    }

    [Fact]
    public async Task AccionNoReversible_SeReportaConSusBytesPerdidos()
    {
        var engine = Build(new RecordingUndoHandler(UndoKind.RegistryValue));

        UndoReport report = await engine.UndoAsync(Journal(
            NotReversible("temp.clean", 4_509_715_660),
            Reversible("power.plan", UndoKind.RegistryValue)));

        Assert.Single(report.Undone);
        Assert.Single(report.NotReversible);
        Assert.Equal(4_509_715_660, report.BytesNotRecoverable);
        // Un borrado no reversible no cuenta como falla: es lo esperado y se informa.
        Assert.True(report.FullySucceeded);
    }

    [Fact]
    public async Task HandlerQueRevienta_NoImpideDeshacerElResto()
    {
        var ok = new RecordingUndoHandler(UndoKind.RegistryValue);
        var boom = new RecordingUndoHandler(UndoKind.PowerPlan, throws: new InvalidOperationException("kaboom"));
        var engine = Build(ok, boom);

        UndoReport report = await engine.UndoAsync(Journal(
            Reversible("a", UndoKind.RegistryValue),
            Reversible("b", UndoKind.PowerPlan),
            Reversible("c", UndoKind.RegistryValue)));

        Assert.Equal(2, report.Undone.Count);
        Assert.Single(report.Failed);
        Assert.Contains("kaboom", report.Failed[0].Error, StringComparison.Ordinal);
        Assert.False(report.FullySucceeded);
    }

    [Fact]
    public async Task HandlerQueDevuelveFalse_SeCuentaComoFalla()
    {
        var engine = Build(new RecordingUndoHandler(UndoKind.ServiceStartMode, returns: false));

        UndoReport report = await engine.UndoAsync(Journal(Reversible("svc", UndoKind.ServiceStartMode)));

        Assert.Empty(report.Undone);
        Assert.Single(report.Failed);
    }

    [Fact]
    public async Task SinHandlerRegistrado_SeReportaComoBugDeConfiguracion()
    {
        var engine = Build(new RecordingUndoHandler(UndoKind.RegistryValue));

        UndoReport report = await engine.UndoAsync(Journal(Reversible("bl", UndoKind.BitLockerResume)));

        Assert.Single(report.NoHandler);
        Assert.False(report.FullySucceeded);
    }

    [Fact]
    public async Task JournalTruncado_DeshaceLoQueSiQuedoRegistrado()
    {
        // Continuación del caso del crash: el journal tiene 2 de 3 acciones y hay que revertir esas 2.
        var handler = new RecordingUndoHandler(UndoKind.RegistryBinaryValue);
        var engine = Build(handler);

        var truncated = new LoadedJournal(
            new JournalHeader { RunId = "r1" },
            new[] { Reversible("a", UndoKind.RegistryBinaryValue), Reversible("b", UndoKind.RegistryBinaryValue) },
            RunState.Running,
            Array.Empty<string>(),
            CorruptLines: new[] { 4 });

        UndoReport report = await engine.UndoAsync(truncated);

        Assert.Equal(2, report.Undone.Count);
        Assert.Equal(2, handler.Received.Count);
    }

    [Fact]
    public async Task JournalVacio_NoHaceNada()
    {
        UndoReport report = await Build().UndoAsync(Journal());

        Assert.Empty(report.Undone);
        Assert.True(report.FullySucceeded);
    }

    [Fact]
    public async Task Cancelacion_SePropaga()
    {
        var engine = Build(new RecordingUndoHandler(UndoKind.RegistryValue));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => engine.UndoAsync(Journal(Reversible("a", UndoKind.RegistryValue)), cts.Token));
    }

    [Fact]
    public void HandlerDuplicado_NoRevienta_GanaElPrimero()
    {
        var first = new RecordingUndoHandler(UndoKind.PowerPlan);
        var second = new RecordingUndoHandler(UndoKind.PowerPlan);

        // Sin el TryAdd, esto tiraría ArgumentException y la app no arrancaría.
        UndoEngine engine = Build(first, second);

        Assert.NotNull(engine);
    }
}
