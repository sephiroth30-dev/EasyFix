using System.Text.Json;

namespace EasyFix.Core.Configuration;

/// <summary>
/// Carga <c>appsettings.json</c>.
/// </summary>
/// <remarks>
/// Busca primero un archivo junto al ejecutable —así se puede ajustar un umbral o agregar una entrada
/// a la lista blanca en el equipo del cliente, sin recompilar— y si no está, usa el recurso
/// embebido. El .exe tiene que funcionar solo desde el USB.
/// </remarks>
public static class OptionsLoader
{
    public const string FileName = "appsettings.json";
    public const string EmbeddedResourceName = "EasyFix.appsettings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parsea desde texto. Es la vía que usan los tests.</summary>
    public static EasyFixOptions Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        EasyFixOptions options =
            JsonSerializer.Deserialize<EasyFixOptions>(json, SerializerOptions)
            ?? throw new InvalidDataException("appsettings.json se parseó como null.");

        return ExpandEnvironmentVariables(options);
    }

    /// <summary>
    /// Expande <c>%SystemRoot%</c> y similares en los prefijos de ruta protegidos.
    /// </summary>
    /// <remarks>
    /// Se hace una sola vez al cargar y no en cada comparación: el clasificador queda sin
    /// dependencias del entorno y por lo tanto testeable con rutas literales.
    /// </remarks>
    private static EasyFixOptions ExpandEnvironmentVariables(EasyFixOptions options)
    {
        IReadOnlyList<string> expanded = options.Classifier.HardBlock.ProtectedPathPrefixes
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();

        return options with
        {
            Classifier = options.Classifier with
            {
                HardBlock = options.Classifier.HardBlock with { ProtectedPathPrefixes = expanded },
            },
        };
    }
}
