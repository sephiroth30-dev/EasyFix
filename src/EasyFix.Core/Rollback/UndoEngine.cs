using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Rollback;

/// <summary>Sabe revertir un tipo de paso.</summary>
public interface IUndoHandler
{
    UndoKind Kind { get; }

    /// <summary>Revierte el paso. Devuelve <c>false</c> si no se pudo, sin tirar excepción.</summary>
    Task<bool> UndoAsync(UndoStep step, CancellationToken ct);
}

/// <param name="Undone">Acciones revertidas con éxito.</param>
/// <param name="NotReversible">Acciones que nunca fueron reversibles (borrado de archivos).</param>
/// <param name="Failed">Acciones reversibles que fallaron al revertirse.</param>
/// <param name="NoHandler">Pasos cuyo tipo no tiene handler registrado — bug de configuración.</param>
/// <param name="BytesNotRecoverable">Bytes borrados que no vuelven. Se muestra al usuario tal cual.</param>
public sealed record UndoReport(
    IReadOnlyList<JournalAction> Undone,
    IReadOnlyList<JournalAction> NotReversible,
    IReadOnlyList<(JournalAction Action, string Error)> Failed,
    IReadOnlyList<JournalAction> NoHandler,
    long BytesNotRecoverable)
{
    public bool FullySucceeded => Failed.Count == 0 && NoHandler.Count == 0;
}

/// <summary>
/// Deshace una corrida a partir de su journal.
/// </summary>
/// <remarks>
/// <para><b>Orden inverso al de aplicación.</b> Los cambios pueden depender entre sí: si se cambió
/// el tipo de inicio de un servicio y después su clave de registro, revertir en el orden original
/// dejaría el sistema en un estado que nunca existió.</para>
///
/// <para><b>Sigue adelante ante errores.</b> Un handler que falla no aborta el resto: revertir 12 de
/// 14 acciones es mejor que revertir 3 y frenar. Lo que falló se reporta.</para>
/// </remarks>
public sealed class UndoEngine
{
    private readonly Dictionary<UndoKind, IUndoHandler> _handlers;
    private readonly ILogger<UndoEngine> _logger;

    public UndoEngine(IEnumerable<IUndoHandler> handlers, ILogger<UndoEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _handlers = new Dictionary<UndoKind, IUndoHandler>();

        foreach (IUndoHandler handler in handlers)
        {
            // Un handler duplicado es un error de registro en el contenedor de DI: gana el primero
            // y se avisa, en vez de que el comportamiento dependa del orden de registro.
            if (!_handlers.TryAdd(handler.Kind, handler))
            {
                _logger.LogWarning(
                    "Hay más de un IUndoHandler para {Kind}; se usa el primero registrado.", handler.Kind);
            }
        }
    }

    public async Task<UndoReport> UndoAsync(LoadedJournal journal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var undone = new List<JournalAction>();
        var notReversible = new List<JournalAction>();
        var failed = new List<(JournalAction, string)>();
        var noHandler = new List<JournalAction>();
        long bytesLost = 0;

        // Orden inverso: se desarma lo último primero.
        foreach (JournalAction action in journal.Actions.Reverse())
        {
            ct.ThrowIfCancellationRequested();

            if (!action.Reversible || action.Undo is null || action.Undo.Kind == UndoKind.None)
            {
                notReversible.Add(action);
                bytesLost += action.FreedBytes ?? 0;
                continue;
            }

            if (!_handlers.TryGetValue(action.Undo.Kind, out IUndoHandler? handler))
            {
                _logger.LogError(
                    "No hay handler para el paso de deshacer {Kind} (fix {FixId}, objetivo {Target}).",
                    action.Undo.Kind, action.FixId, action.Target);
                noHandler.Add(action);
                continue;
            }

            try
            {
                bool ok = await handler.UndoAsync(action.Undo, ct).ConfigureAwait(false);
                if (ok)
                {
                    undone.Add(action);
                    _logger.LogInformation("Deshecho: {FixId} sobre {Target}.", action.FixId, action.Target);
                }
                else
                {
                    failed.Add((action, "El handler devolvió false."));
                    _logger.LogWarning("No se pudo deshacer {FixId} sobre {Target}.", action.FixId, action.Target);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A propósito se atrapa todo: un handler que revienta no puede impedir que se
                // reviertan las demás acciones.
                failed.Add((action, $"{ex.GetType().Name}: {ex.Message}"));
                _logger.LogError(ex, "Error al deshacer {FixId} sobre {Target}.", action.FixId, action.Target);
            }
        }

        return new UndoReport(undone, notReversible, failed, noHandler, bytesLost);
    }
}
