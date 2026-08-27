using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Rollback;

/// <param name="RunId">Identificador de la corrida.</param>
/// <param name="Path">Ruta del archivo <c>.jsonl</c>.</param>
/// <param name="ModifiedUtc">Última escritura, que es cuándo terminó de aplicarse.</param>
/// <param name="Journal">Contenido ya parseado.</param>
public sealed record StoredRun(string RunId, string Path, DateTimeOffset ModifiedUtc, LoadedJournal Journal)
{
    /// <summary>Cuántas acciones se pueden revertir.</summary>
    public int ReversibleCount => Journal.Actions.Count(a => a.Reversible && a.Undo is not null);

    /// <summary>Acciones que no se pueden revertir, típicamente borrado de archivos.</summary>
    public int IrreversibleCount => Journal.Actions.Count(a => !a.Reversible || a.Undo is null);

    public bool CanUndo => ReversibleCount > 0;
}

/// <summary>
/// Encuentra y lee los journals de corridas anteriores.
/// </summary>
/// <remarks>
/// <para>Existe para que «Deshacer todo» pueda funcionar <b>entre sesiones</b>: el técnico repara hoy,
/// cierra la app, y la semana que viene vuelve al mismo equipo y necesita revertir. Los journals viven
/// en <c>%ProgramData%</c> justamente por eso — la app corre desde un USB que se retira.</para>
///
/// <para>Un journal ilegible no impide leer los demás: se saltea y se registra.</para>
/// </remarks>
public sealed class JournalStore
{
    private readonly string _directory;
    private readonly ILogger<JournalStore> _logger;

    public JournalStore(ILogger<JournalStore> logger, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _directory = directory ?? FileJournalSink.DefaultDirectory;
    }

    public string Directory => _directory;

    /// <summary>
    /// Corridas encontradas, de la más reciente a la más antigua.
    /// </summary>
    public IReadOnlyList<StoredRun> List(int limit = 20)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return Array.Empty<StoredRun>();
        }

        List<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(_directory, "*.jsonl")
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo listar los journals en {Directory}.", _directory);
            return Array.Empty<StoredRun>();
        }

        var runs = new List<StoredRun>(files.Count);

        foreach (string file in files)
        {
            try
            {
                LoadedJournal journal = JournalReader.Parse(System.IO.File.ReadLines(file));

                if (!journal.IsUsable)
                {
                    // Sin encabezado no se sabe de qué corrida es. Se informa y se sigue.
                    _logger.LogWarning(
                        "El journal {File} no tiene encabezado legible ({Corrupt} línea(s) corrupta(s)).",
                        file, journal.CorruptLines.Count);
                    continue;
                }

                runs.Add(new StoredRun(
                    journal.Header!.RunId,
                    file,
                    new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(file), TimeSpan.Zero),
                    journal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "No se pudo leer el journal {File}.", file);
            }
        }

        return runs;
    }

    /// <summary>
    /// La corrida más reciente que tenga algo que revertir.
    /// </summary>
    /// <remarks>
    /// Se salta las corridas que solo tienen acciones irreversibles —una limpieza de temporales, por
    /// ejemplo— porque ofrecer deshacerlas sería mentir. También se saltan las ya deshechas.
    /// </remarks>
    public StoredRun? FindLatestUndoable() =>
        List().FirstOrDefault(r => r.CanUndo && r.Journal.State != RunState.UndoneByUser);

    /// <summary>
    /// Marca una corrida como deshecha, para no volver a ofrecerla.
    /// </summary>
    /// <remarks>
    /// Se agrega una línea al mismo archivo en vez de borrarlo: el journal es la constancia de lo que
    /// se le hizo al equipo y de lo que se revirtió. Borrarlo perdería el historial.
    /// </remarks>
    public bool MarkUndone(StoredRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            using var sink = new FileJournalSink(run.Path);
            sink.WriteLine(
                System.Text.Json.JsonSerializer.Serialize(new JournalRecord
                {
                    Kind = JournalRecord.KindState,
                    State = RunState.UndoneByUser,
                }));

            _logger.LogInformation("Corrida {RunId} marcada como deshecha.", run.RunId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo marcar {RunId} como deshecha.", run.RunId);
            return false;
        }
    }
}
