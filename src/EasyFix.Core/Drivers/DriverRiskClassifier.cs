namespace EasyFix.Core.Drivers;

/// <summary>
/// Decide la <see cref="DriverRiskClass"/> de una actualización a partir de su título y categorías.
/// Lógica pura: sin Windows, testeable con strings de ejemplo.
/// </summary>
/// <remarks>
/// <para>Windows Update arma el título de un driver como <c>"Fabricante - Clase - versión"</c> — el
/// tramo <c>Clase</c> es el nombre amigable de la <i>Setup Class</i> del dispositivo (la misma
/// taxonomía que usa el Administrador de dispositivos para agrupar: Red, Pantallas, Sonido, etc.,
/// en inglés en el dato crudo: <c>Net</c>, <c>Display</c>, <c>Media</c>...). Es información que
/// Microsoft ya catalogó y validó; acá solo se la interpreta.</para>
///
/// <para><b>Nunca se verificó contra una búsqueda real de Windows Update.</b> Los tokens de clase
/// vienen de la documentación pública de las Setup Classes de Windows, no de un equipo real. Si en
/// la práctica un título viene con un formato distinto, esta clase lo manda a
/// <see cref="DriverRiskClass.NeedsConfirmation"/> por descarte — nunca a <c>Safe</c> por
/// descarte. Esa asimetría es la que importa: un falso negativo (algo seguro que pide confirmación
/// de más) es una molestia; un falso positivo (algo riesgoso que se instala solo) no lo es.</para>
/// </remarks>
public static class DriverRiskClassifier
{
    /// <summary>
    /// Tokens de Setup Class que se consideran de bajo impacto. Allowlist a propósito: todo lo que
    /// no esté acá explícitamente no es "Safe", sin importar cuánto se le parezca.
    /// </summary>
    private static readonly string[] SafeClassTokens =
    {
        "Net", "Display", "Media", "USB", "Bluetooth", "Printer", "PrintQueue",
        "HIDClass", "Keyboard", "Mouse", "Camera", "Image", "SmartCardReader", "Sensor",
    };

    /// <summary>
    /// Palabras que, en cualquier parte del título o las categorías, descartan todo lo demás:
    /// firmware nunca se ofrece instalar, sin importar qué más diga el título.
    /// </summary>
    private static readonly string[] FirmwareKeywords = { "firmware", "bios", "uefi" };

    public static DriverRiskClass Classify(string title, IReadOnlyList<string>? categories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        string haystack = categories is { Count: > 0 }
            ? $"{title} {string.Join(' ', categories)}"
            : title;

        if (ContainsAny(haystack, FirmwareKeywords))
        {
            return DriverRiskClass.Firmware;
        }

        string? classToken = ExtractClassToken(title);

        if (classToken is not null && SafeClassTokens.Any(
                t => string.Equals(t, classToken, StringComparison.OrdinalIgnoreCase)))
        {
            return DriverRiskClass.Safe;
        }

        // classToken no matcheó la lista blanca (puede ser almacenamiento, o cualquier clase que
        // todavía no está en la lista), o el título no vino en el formato esperado. En los dos
        // casos: fallar cerrado, pedir confirmación.
        return DriverRiskClass.NeedsConfirmation;
    }

    /// <summary>
    /// El segundo tramo de <c>"Fabricante - Clase - versión"</c>. <c>null</c> si el título no trae
    /// al menos dos separadores " - " — no se arriesga a interpretar un formato que no es el
    /// esperado.
    /// </summary>
    private static string? ExtractClassToken(string title)
    {
        string[] parts = title.Split(" - ", StringSplitOptions.TrimEntries);
        return parts.Length >= 3 ? parts[1] : null;
    }

    private static bool ContainsAny(string haystack, IReadOnlyList<string> needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
