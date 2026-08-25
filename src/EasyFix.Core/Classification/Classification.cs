namespace EasyFix.Core.Classification;

/// <summary>
/// Las tres capas de decisión. El orden numérico es el orden de evaluación, y la primera que
/// aplica gana.
/// </summary>
public enum ClassificationTier
{
    /// <summary>Intocable. Producto de seguridad, componente de Windows, dependencia o driver.</summary>
    HardBlocked = 1,

    /// <summary>Lista blanca positiva: lo único que se desactiva sin preguntar.</summary>
    AutoSafe = 2,

    /// <summary>
    /// Todo lo demás. Checkbox, nunca automático. Es el <b>default</b>:
    /// desconocido = pedir permiso.
    /// </summary>
    NeedsApproval = 3,
}

/// <param name="Tier">La capa que le tocó.</param>
/// <param name="Reason">Por qué, en español, para mostrárselo al cliente.</param>
/// <param name="WhatYouLose">Qué deja de funcionar si se desactiva. Solo para <see cref="ClassificationTier.AutoSafe"/> y NeedsApproval.</param>
/// <param name="PossibleMalware">Binario sin firma válida: se marca en rojo en la UI.</param>
public sealed record Classification(
    ClassificationTier Tier,
    string Reason,
    string? WhatYouLose = null,
    bool PossibleMalware = false)
{
    public bool CanDisableAutomatically => Tier == ClassificationTier.AutoSafe;
}
