using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests del localizador de journals, contra archivos reales.
/// </summary>
/// <remarks>
/// Es lo que permite que «Deshacer todo» funcione entre sesiones: el técnico repara, cierra la app, y
/// vuelve al equipo la semana siguiente.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class JournalStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly JournalStore _store;

    public JournalStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "easyfix-store-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_dir);
        _store = new JournalStore(NullLogger<JournalStore>.Instance, _dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Escribe un journal con las acciones indicadas.</summary>
    private string WriteRun(
        string runId,
        long? restorePoint = 42,
        int reversible = 1,
        int irreversible = 0,
        long freedBytes = 0,
        RunState? finalState = RunState.Completed)
    {
        string path = FileJournalSink.PathForRun(runId, _dir);

        using var sink = new FileJournalSink(path);
        RunJournal journal = RunJournal.Start(
            new JournalHeader { RunId = runId, RestorePointSequence = restorePoint }, sink);

        for (int i = 0; i < reversible; i++)
        {
            journal.Append(new JournalAction
            {
                FixId = "startup.disable",
                Target = $"Programa{i}",
                Reversible = true,
                Undo = new UndoStep { Kind = UndoKind.RegistryBinaryValue },
            });
        }

        for (int i = 0; i < irreversible; i++)
        {
            journal.Append(new JournalAction
            {
                FixId = "temp.clean",
                Target = @"C:\Windows\Temp",
                Reversible = false,
                FreedBytes = freedBytes,
            });
        }

        if (finalState is RunState state)
        {
            journal.SetState(state);
        }

        return path;
    }

    [Fact]
    public void SinCarpeta_DevuelveListaVacia()
    {
        var store = new JournalStore(
            NullLogger<JournalStore>.Instance, Path.Combine(_dir, "no-existe"));

        Assert.Empty(store.List());
        Assert.Null(store.FindLatestUndoable());
    }

    [Fact]
    public void ListaLasCorridas()
    {
        WriteRun("run-a");
        WriteRun("run-b");

        Assert.Equal(2, _store.List().Count);
    }

    [Fact]
    public void CuentaLasAccionesReversiblesYLasQueNo()
    {
        WriteRun("run", reversible: 3, irreversible: 2, freedBytes: 1_048_576);

        StoredRun run = Assert.Single(_store.List());

        Assert.Equal(3, run.ReversibleCount);
        Assert.Equal(2, run.IrreversibleCount);
        Assert.True(run.CanUndo);
    }

    [Fact]
    public void UnaCorridaSoloConAccionesIrreversibles_NoSeOfreceParaDeshacer()
    {
        // Ofrecer deshacer una limpieza de temporales sería mentir: los archivos no vuelven.
        WriteRun("solo-limpieza", reversible: 0, irreversible: 3, freedBytes: 1000);

        Assert.Null(_store.FindLatestUndoable());
    }

    [Fact]
    public void UnaCorridaYaDeshecha_NoSeVuelveAOfrecer()
    {
        WriteRun("ya-deshecha", reversible: 2, finalState: RunState.UndoneByUser);

        Assert.Null(_store.FindLatestUndoable());
    }

    [Fact]
    public void MarkUndone_LaSacaDeLasCandidatas()
    {
        WriteRun("run", reversible: 2);

        StoredRun? target = _store.FindLatestUndoable();
        Assert.NotNull(target);

        Assert.True(_store.MarkUndone(target!));

        // Se relee del disco: la marca tiene que haber quedado persistida.
        Assert.Null(_store.FindLatestUndoable());
    }

    [Fact]
    public void MarkUndone_NoBorraElHistorial()
    {
        // El journal es la constancia de lo que se le hizo al equipo. Marcar no es borrar.
        WriteRun("run", reversible: 2);
        StoredRun target = _store.FindLatestUndoable()!;

        _store.MarkUndone(target);

        StoredRun stored = Assert.Single(_store.List());
        Assert.Equal(2, stored.ReversibleCount);
        Assert.Equal(RunState.UndoneByUser, stored.Journal.State);
    }

    [Fact]
    public void UnJournalIlegible_NoImpideLeerLosDemas()
    {
        WriteRun("bueno", reversible: 1);
        File.WriteAllText(Path.Combine(_dir, "roto.jsonl"), "{esto no es json\nni esto tampoco");

        // El bueno se lee; el roto se saltea con una advertencia.
        StoredRun run = Assert.Single(_store.List());
        Assert.Equal("bueno", run.RunId);
    }

    [Fact]
    public void ConservaLaSecuenciaDelPuntoDeRestauracion()
    {
        // La pantalla de deshacer lo muestra: saber si hubo respaldo cambia la decisión del técnico.
        WriteRun("con-punto", restorePoint: 217);

        StoredRun run = Assert.Single(_store.List());
        Assert.Equal(217, run.Journal.Header!.RestorePointSequence);
    }

    [Fact]
    public void UnaCorridaSinPuntoDeRestauracion_SeDistingue()
    {
        WriteRun("sin-punto", restorePoint: null);

        StoredRun run = Assert.Single(_store.List());
        Assert.Null(run.Journal.Header!.RestorePointSequence);
    }

    [Fact]
    public void LaCorridaSinEstadoFinal_TambienSeOfrece()
    {
        // Una corrida interrumpida por un crash no tiene línea de estado. Es justo la que más
        // probablemente haya que deshacer.
        WriteRun("interrumpida", reversible: 1, finalState: null);

        Assert.NotNull(_store.FindLatestUndoable());
    }
}
