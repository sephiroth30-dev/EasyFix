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
/// <para><b>Qué está verificado y qué no.</b> Cuatro de estos valores se confirmaron contra un equipo
/// real, porque aparecieron en sus logs: <see cref="NoApplicationsFound"/>,
/// <see cref="UpdateNotApplicable"/>, <see cref="SourceDataMissing"/> e
/// <see cref="InstallerHashMismatch"/>. Los otros tres —<see cref="PackageAlreadyInstalled"/>,
/// <see cref="NoApplicableInstaller"/> y <see cref="FailedToOpenAllSources"/>— <b>siguen
/// transcritos de memoria y sin comprobar</b>. Están marcados uno por uno abajo. Decir que todos
/// estaban verificados, como decía este comentario antes, era exactamente el tipo de afirmación no
/// medida que este proyecto trata de no cometer.</para>
///
/// <para><b>Por qué eso ya no es peligroso.</b> Un código sin reconocer cae en
/// <c>WingetOutcome.Failed</c> con su valor en crudo, así que el peor caso de una constante
/// equivocada es un fallo honesto y diagnosticable. La única vía por la que un código podía volverse
/// un <em>éxito</em> falso era «ya estaba instalado», y ahora eso exige confirmación independiente:
/// ver <see cref="ConfirmsAlreadyInstalled"/>.</para>
///
/// <para>Como respaldo, <see cref="WingetErrorTable"/> carga la tabla completa del winget instalado en
/// el equipo con <c>winget error --output</c>, así el mapeo lo provee winget y no la memoria de nadie.
/// Ojo: ese comando no existe en winget 1.24 y anteriores —devuelve
/// <c>INVALID_CL_ARGUMENTS</c>—, así que en equipos viejos las constantes son la única fuente.</para>
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

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED</c>. <b>SIN VERIFICAR.</b>
    /// </summary>
    /// <remarks>
    /// Transcrito de memoria y nunca visto en un log. No se usa para declarar éxito por sí solo: ver
    /// <see cref="ConfirmsAlreadyInstalled"/>. Para confirmarlo hace falta la salida de
    /// <c>winget error --output</c> de un equipo con winget 1.6 o superior.
    /// </remarks>
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150056);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER</c>. <b>SIN VERIFICAR.</b>
    /// </summary>
    /// <remarks>
    /// Si está mal, el efecto es que un «sin instalador compatible» se reporta como fallo genérico con
    /// el código a la vista. Molesto, no peligroso.
    /// </remarks>
    public const int NoApplicableInstaller = unchecked((int)0x8A150061);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_SOURCE_DATA_MISSING</c> — falta la metadata del catálogo.
    /// </summary>
    /// <remarks>
    /// Es el error de la sesión elevada: el paquete <c>Microsoft.Winget.Source</c> se instala por
    /// usuario y la cuenta de administrador no lo tiene. Ver microsoft/winget-cli#698.
    /// </remarks>
    public const int SourceDataMissing = unchecked((int)0x8A15000F);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_FAILED_TO_OPEN_ALL_SOURCES</c>. <b>SIN VERIFICAR, y sospechoso.</b>
    /// </summary>
    /// <remarks>
    /// <c>0x8A150019</c> podría ser <c>NOT_ALL_QUERIES_FOUND_SINGLE</c>, que es otra cosa. Si está
    /// mal, el efecto es que EasyFix intenta reparar las fuentes cuando no hacía falta: el reset es
    /// idempotente y el resultado real lo decide el reintento, así que no rompe nada. Confirmar con
    /// <c>winget error --output</c>.
    /// </remarks>
    public const int FailedToOpenAllSources = unchecked((int)0x8A150019);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_INSTALLER_HASH_MISMATCH</c> — el instalador descargado no coincide
    /// con el hash del manifiesto.
    /// </summary>
    /// <remarks>
    /// Verificado en un equipo real: Google Chrome falló con este código. Suele ser una descarga
    /// corrupta, así que <b>merece reintento</b>. Si persiste, el manifiesto del repositorio está
    /// desactualizado respecto del instalador que publica el fabricante y no hay nada que hacer del
    /// lado del equipo.
    /// </remarks>
    public const int InstallerHashMismatch = unchecked((int)0x8A150011);

    /// <summary>
    /// Códigos que valen la pena reintentar: fallos transitorios de red o de descarga.
    /// </summary>
    public static bool IsWorthRetrying(int code) =>
        code == InstallerHashMismatch;

    /// <summary>Códigos que indican que el catálogo no se puede leer y conviene repararlo.</summary>
    public static bool IsSourceProblem(int code) =>
        code == SourceDataMissing || code == FailedToOpenAllSources;

    /// <summary>
    /// Símbolos de winget que significan «ya estaba instalado».
    /// </summary>
    /// <remarks>
    /// Los <b>nombres</b> son estables entre versiones de winget; los <b>números</b> no lo son para
    /// nosotros, porque acá se transcriben a mano. Cuando la tabla del equipo está cargada, se decide
    /// por el símbolo y las constantes dejan de importar.
    /// </remarks>
    private static readonly string[] AlreadyInstalledSymbols =
    {
        "PACKAGE_ALREADY_INSTALLED",
        "UPDATE_NOT_APPLICABLE",
        "NO_APPLICATIONS_FOUND_FOR_UPGRADE",
    };

    /// <summary>
    /// Códigos que <em>probablemente</em> significan «ya está instalado», según nuestras constantes.
    /// </summary>
    /// <remarks>
    /// <b>No alcanza para declarar éxito por sí solo.</b> Ver
    /// <see cref="ConfirmsAlreadyInstalled(int, WingetErrorTable?, string)"/>.
    /// </remarks>
    public static bool LooksAlreadyInstalled(int code) =>
        code == PackageAlreadyInstalled || code == UpdateNotApplicable;

    /// <summary>
    /// <c>true</c> solo cuando hay evidencia real de que el paquete ya estaba instalado.
    /// </summary>
    /// <remarks>
    /// <para><b>Por qué existe esta función y no basta con comparar el código.</b> «Ya estaba
    /// instalado» se reporta como éxito: el paquete cuenta como disponible en el equipo. Entonces una
    /// constante equivocada acá no produce un fallo falso —molesto pero visible— sino un <b>éxito
    /// falso</b>: EasyFix le diría al técnico que un programa quedó instalado cuando winget devolvió
    /// un error que ni reconocimos. De todos los bugs posibles en esta herramienta, ese es el peor,
    /// porque no deja rastro que haga sospechar.</para>
    ///
    /// <para>Y el riesgo es concreto: dos de las constantes de esta clase ya estuvieron mal una vez,
    /// y las de «ya instalado» no se pudieron verificar contra un Windows todavía —el equipo del
    /// 2026-08-29 traía winget 1.24, cuyo <c>winget error --output</c> devuelve
    /// <c>INVALID_CL_ARGUMENTS</c>, así que la tabla quedó vacía y las constantes fueron la única
    /// fuente.</para>
    ///
    /// <para><b>La regla.</b> El código por sí solo no autoriza. Hace falta que lo confirme la tabla
    /// del propio winget del equipo (por el nombre del símbolo, que sí es estable) o la salida de
    /// winget. Sin confirmación, se reporta el fallo con el código en crudo: honesto y diagnosticable.
    /// Es el mismo criterio que el resto del proyecto — un dato que no se pudo medir nunca se cuenta
    /// como verde.</para>
    /// </remarks>
    /// <param name="code">Código de salida de winget.</param>
    /// <param name="table">Tabla del equipo, si se pudo cargar.</param>
    /// <param name="standardOutput">Lo que winget escribió; ahí avisa en texto.</param>
    public static bool ConfirmsAlreadyInstalled(
        int code, WingetErrorTable? table, string standardOutput)
    {
        // 1. La tabla del equipo manda: el símbolo lo provee winget, no nosotros.
        if (table?.SymbolFor(code) is { } symbol)
        {
            return AlreadyInstalledSymbols.Any(known =>
                symbol.Contains(known, StringComparison.Ordinal));
        }

        // 2. Sin tabla, la constante sirve solo si winget además lo dice por texto.
        return LooksAlreadyInstalled(code) && SaysAlreadyInstalled(standardOutput);
    }

    /// <summary>
    /// <c>true</c> si la salida de winget dice que ya estaba instalado.
    /// </summary>
    /// <remarks>
    /// En español y en inglés: el equipo del cliente puede tener cualquiera de los dos, y comparar
    /// solo contra el inglés es el mismo error que ya costó cuatro minutos por corrida en la detección
    /// de corrupción de DISM.
    /// </remarks>
    public static bool SaysAlreadyInstalled(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return false;
        }

        string[] phrases =
        {
            "already installed",
            "ya está instalado",
            "ya esta instalado",
            "No newer package versions",
            "No hay versiones más recientes",
            "No applicable upgrade",
            "no aplicable",
        };

        return phrases.Any(p => standardOutput.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
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
