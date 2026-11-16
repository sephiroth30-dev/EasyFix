using EasyFix.Core.Network;

namespace EasyFix.Core.Verification;

/// <summary>Estado de una condición de la lista de verificación.</summary>
public enum ChecklistStatus
{
    /// <summary>
    /// No se pudo evaluar. Se muestra tal cual, nunca como verde: una comprobación que falló no es
    /// evidencia de que el equipo esté sano.
    /// </summary>
    NotDetermined = 0,

    Pass,
    Fail,
}

/// <summary>Una condición evaluada, con el motivo del estado.</summary>
/// <param name="Id">Identificador estable, el mismo antes y después para poder emparejar columnas.</param>
/// <param name="Title">La condición en español, redactada como estado deseado: «Salud del disco».</param>
/// <param name="Status">Verde, rojo o no determinado.</param>
/// <param name="Reason">El número o hecho medido detrás del estado. Sin esto es una opinión.</param>
/// <param name="NeedsHardware"><c>true</c> cuando un Fail no se arregla con software.</param>
public sealed record ChecklistItem(
    string Id,
    string Title,
    ChecklistStatus Status,
    string Reason,
    bool NeedsHardware = false);

/// <summary>El resultado de evaluar la lista completa en un momento dado.</summary>
public sealed record ChecklistReport(IReadOnlyList<ChecklistItem> Items)
{
    public int PassCount => Items.Count(i => i.Status == ChecklistStatus.Pass);
    public int FailCount => Items.Count(i => i.Status == ChecklistStatus.Fail);
    public int NotDeterminedCount => Items.Count(i => i.Status == ChecklistStatus.NotDetermined);
}

/// <summary>Las dos columnas: el mismo checklist evaluado antes y después de los arreglos.</summary>
/// <remarks>
/// Es el entregable real de la herramienta — lo que convierte «te lo dejé sin errores» en algo
/// verificable. Los items se emparejan por <see cref="ChecklistItem.Id"/>; si una condición pasó de
/// Fail a Pass, eso ES la prueba de que el arreglo funcionó. Si quedó igual, se dice. No hay
/// porcentajes inventados.
/// </remarks>
public sealed record BeforeAfterChecklist(ChecklistReport Before, ChecklistReport After)
{
    /// <summary>Filas emparejadas para la tabla de dos columnas, en el orden del checklist.</summary>
    public IReadOnlyList<(ChecklistItem Before, ChecklistItem? After)> Rows =>
        Before.Items
            .Select(b => (b, After.Items.FirstOrDefault(a => a.Id == b.Id)))
            .ToList();

    public int ImprovedCount => Rows.Count(r =>
        r.Before.Status == ChecklistStatus.Fail && r.After?.Status == ChecklistStatus.Pass);

    public int StillFailingCount => Rows.Count(r => r.After?.Status == ChecklistStatus.Fail);

    /// <summary>Condiciones que EMPEORARON (verde → rojo). No son «siguen en rojo»: son peores.</summary>
    public int RegressedCount => Rows.Count(r =>
        r.Before.Status == ChecklistStatus.Pass && r.After?.Status == ChecklistStatus.Fail);

    /// <summary>
    /// Condiciones que salieron del rojo SIN pasar a verde (rojo → no determinado). No cuentan como
    /// mejora: que la re-medición fallara no es evidencia de que el problema se fue.
    /// </summary>
    public int LostEvidenceCount => Rows.Count(r =>
        r.Before.Status == ChecklistStatus.Fail && r.After?.Status == ChecklistStatus.NotDetermined);
}
