using System.Text.RegularExpressions;

namespace EasyFix.Core.Apps;

/// <summary>
/// Valida IDs de paquete de winget antes de pasarlos a un proceso.
/// </summary>
/// <remarks>
/// <para>Los IDs vienen de <c>appsettings.json</c>, que es editable. Aunque
/// <see cref="Processes.SafeProcessRunner"/> ya impide la inyección por shell, validar acá es
/// defensa en profundidad: un ID basura tiene que fallar de forma clara y temprana, no convertirse en
/// una llamada rara a winget con un mensaje de error incomprensible.</para>
///
/// <para>El <c>+</c> está permitido a propósito: IDs reales como
/// <c>Microsoft.VCRedist.2015+.x64</c> lo usan.</para>
/// </remarks>
public static partial class WingetPackageId
{
    /// <summary>Timeout del regex: es un patrón lineal, pero nunca se compila sin límite.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && Pattern().IsMatch(id);

    /// <summary>Devuelve el ID validado o tira excepción. Para el borde del sistema.</summary>
    public static string Validate(string? id)
    {
        if (!IsValid(id))
        {
            throw new ArgumentException(
                $"'{id}' no es un ID de paquete de winget válido. Se aceptan letras, números y . _ + -",
                nameof(id));
        }

        return id!;
    }
}
