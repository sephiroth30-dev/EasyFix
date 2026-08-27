namespace EasyFix.Core.Apps;

/// <summary>
/// Códigos de salida de winget.
/// </summary>
/// <remarks>
/// <para><b>Por qué esta clase existe aparte.</b> La primera versión hardcodeaba constantes sacadas de
/// memoria y dos estaban mal, con consecuencias visibles en la primera prueba real: 7-Zip y Visual C++
/// Redistributable se reportaron como «no encontrado» cuando en realidad <b>ya estaban instalados</b>
/// —los había instalado la propia app dos horas antes—. Un fallo falso es peor que un error: hace
/// desconfiar de lo que sí funciona.</para>
///
/// <para>Los valores de acá están verificados contra la documentación de winget-cli. Y como respaldo,
/// <see cref="WingetErrorTable"/> puede cargar la tabla completa del winget instalado en el equipo con
/// <c>winget error --output</c>, así el mapeo lo provee winget y no la memoria de nadie.</para>
/// </remarks>
public static class WingetErrorCodes
{
    public const int Success = 0;

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND</c> — el ID no existe en el catálogo.
    /// </summary>
    /// <remarks>
    /// Verificado en la prueba real: RustDesk devolvió este código con el mensaje «No se encontró
    /// ningún paquete coincidente con los criterios de búsqueda», que confirma que fue removido del
    /// repositorio. Antes esta constante estaba asignada a un error de fuentes.
    /// </remarks>
    public const int NoApplicationsFound = unchecked((int)0x8A150014);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE</c> — no hay actualización que aplicar.
    /// </summary>
    /// <remarks>
    /// <b>Sobre un <c>install</c> esto significa que el paquete ya está instalado en su última
    /// versión.</b> No es un fallo. Es el código que en la primera prueba se reportaba como «no
    /// encontrado» y arruinaba el reporte.
    /// </remarks>
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);

    /// <summary><c>APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED</c>.</summary>
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150056);

    /// <summary><c>APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER</c>.</summary>
    public const int NoApplicableInstaller = unchecked((int)0x8A150061);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_SOURCE_DATA_MISSING</c> — falta la metadata del catálogo.
    /// </summary>
    /// <remarks>
    /// Es el error de la sesión elevada: el paquete <c>Microsoft.Winget.Source</c> se instala por
    /// usuario y la cuenta de administrador no lo tiene. Ver microsoft/winget-cli#698.
    /// </remarks>
    public const int SourceDataMissing = unchecked((int)0x8A15000F);

    /// <summary><c>APPINSTALLER_CLI_ERROR_FAILED_TO_OPEN_ALL_SOURCES</c>.</summary>
    public const int FailedToOpenAllSources = unchecked((int)0x8A150019);

    /// <summary>Códigos que indican que el catálogo no se puede leer y conviene repararlo.</summary>
    public static bool IsSourceProblem(int code) =>
        code == SourceDataMissing || code == FailedToOpenAllSources;

    /// <summary>Códigos que significan «ya está instalado», que no es un fallo.</summary>
    public static bool MeansAlreadyInstalled(int code) =>
        code == PackageAlreadyInstalled || code == UpdateNotApplicable;
}

/// <summary>
/// Tabla de códigos leída del winget del equipo.
/// </summary>
/// <remarks>
/// <c>winget error --output &lt;archivo&gt;</c> exporta todos los códigos con su símbolo y
/// descripción. Cargarla en runtime hace que el diagnóstico no dependa de constantes que puedan estar
/// desactualizadas respecto de la versión instalada. Si el comando no existe en esa versión de winget,
/// se usan las constantes de <see cref="WingetErrorCodes"/>.
/// </remarks>
public sealed class WingetErrorTable
{
    private readonly Dictionary<int, (string Symbol, string Description)> _entries = new();

    /// <summary><c>true</c> si se pudo cargar la tabla del equipo.</summary>
    public bool IsLoaded => _entries.Count > 0;

    public int Count => _entries.Count;

    /// <summary>Símbolo del código, p. ej. <c>APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE</c>.</summary>
    public string? SymbolFor(int code) =>
        _entries.TryGetValue(code, out (string Symbol, string Description) entry) ? entry.Symbol : null;

    /// <summary>Descripción oficial del código.</summary>
    public string? DescriptionFor(int code) =>
        _entries.TryGetValue(code, out (string Symbol, string Description) entry) ? entry.Description : null;

    /// <summary>
    /// Parsea la salida de <c>winget error --output</c>.
    /// </summary>
    /// <remarks>
    /// El formato es tabular y su orden de columnas puede cambiar entre versiones, así que en vez de
    /// asumir posiciones se busca en cada línea el primer hexadecimal de 8 dígitos —el código— y el
    /// primer identificador en mayúsculas que empiece con <c>APPINSTALLER_</c> —el símbolo—. El resto
    /// de la línea es la descripción. Tolerante a cambios de formato por diseño.
    /// </remarks>
    public static WingetErrorTable Parse(string text)
    {
        var table = new WingetErrorTable();

        if (string.IsNullOrWhiteSpace(text))
        {
            return table;
        }

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int? code = ExtractHexCode(trimmed);
            if (code is null)
            {
                continue;
            }

            string? symbol = ExtractSymbol(trimmed);
            if (symbol is null)
            {
                continue;
            }

            // La descripción es lo que queda después del símbolo.
            int afterSymbol = trimmed.IndexOf(symbol, StringComparison.Ordinal) + symbol.Length;
            string description = trimmed[afterSymbol..].Trim(' ', '\t', '-', '|', '\r');

            table._entries[code.Value] = (symbol, description);
        }

        return table;
    }

    private static int? ExtractHexCode(string line)
    {
        int at = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        while (at >= 0 && at + 10 <= line.Length)
        {
            string candidate = line.Substring(at + 2, 8);
            if (candidate.All(Uri.IsHexDigit) &&
                uint.TryParse(candidate, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint value))
            {
                return unchecked((int)value);
            }

            at = line.IndexOf("0x", at + 2, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static string? ExtractSymbol(string line)
    {
        const string Prefix = "APPINSTALLER_";

        int start = line.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int end = start;
        while (end < line.Length && (char.IsAsciiLetterUpper(line[end]) || char.IsAsciiDigit(line[end]) || line[end] == '_'))
        {
            end++;
        }

        return end > start + Prefix.Length ? line[start..end] : null;
    }
}
