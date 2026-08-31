using EasyFix.Core.Abstractions;
using EasyFix.Core.Network;

namespace EasyFix.Core.Apps;

/// <summary>Desenlace de intentar instalar un paquete.</summary>
public enum WingetOutcome
{
    /// <summary>Instalado en esta corrida.</summary>
    Installed,

    /// <summary>Ya estaba instalado. <b>No es un fallo</b> y no debe contarse como tal.</summary>
    AlreadyInstalled,

    /// <summary>El ID no existe en el repositorio, o cambió de nombre.</summary>
    NotFound,

    /// <summary>No hay instalador para la arquitectura o la versión de Windows de este equipo.</summary>
    NoApplicableInstaller,

    /// <summary>Falta winget en el equipo: hay que hacer el bootstrap.</summary>
    WingetMissing,

    /// <summary>
    /// winget está pero no puede leer sus fuentes. Pasa al correr elevado: el paquete de fuentes se
    /// instala por usuario y la sesión de administrador no lo tiene.
    /// </summary>
    SourceUnavailable,

    /// <summary>
    /// El ID no existe en el catálogo de winget. Hay que corregirlo en <c>appsettings.json</c> o
    /// configurar una descarga directa.
    /// </summary>
    NotInCatalog,

    /// <summary>
    /// El equipo no tiene internet. Ningún instalador se llegó a lanzar.
    /// </summary>
    /// <remarks>
    /// Existe separado de <see cref="Failed"/> porque no es un fallo del paquete ni de winget: es una
    /// precondición que no se cumple. Mezclarlos fue lo que llevó a culpar tres veces al catálogo de
    /// winget de un equipo que estaba sin red.
    /// </remarks>
    NoNetwork,

    /// <summary>Se cortó por timeout.</summary>
    TimedOut,

    /// <summary>Falló. <see cref="WingetResult.Detail"/> tiene el código en crudo.</summary>
    Failed,
}

/// <param name="PackageId">Qué paquete.</param>
/// <param name="Outcome">Cómo salió.</param>
/// <param name="Detail">Explicación en español, con el código en crudo cuando corresponde.</param>
/// <param name="ExitCode">Código de salida real, para el log.</param>
public sealed record WingetResult(
    string PackageId,
    WingetOutcome Outcome,
    string Detail,
    int? ExitCode)
{
    /// <summary>
    /// <c>true</c> cuando el paquete quedó disponible en el equipo, sin importar si lo instalamos
    /// nosotros o ya estaba.
    /// </summary>
    public bool PackageAvailable => Outcome is WingetOutcome.Installed or WingetOutcome.AlreadyInstalled;
}

/// <summary>
/// Interpreta el resultado de <c>winget install</c>.
/// </summary>
/// <remarks>
/// <para><b>Por qué no basta con "terminó sin excepción".</b> winget devuelve códigos distintos para
/// "instalado", "ya estaba instalado" y "no encontré el paquete". Tratarlos todos igual haría que el
/// reporte le dijera al técnico que instaló siete programas cuando instaló cuatro.</para>
///
/// <para><b>Los códigos están pendientes de verificación.</b> Salen de la documentación de winget y
/// no se pudieron comprobar todavía contra un Windows real; el spike de la fase 1 los imprime. El
/// diseño está hecho para que eso no importe demasiado: un código no reconocido cae en
/// <see cref="WingetOutcome.Failed"/> con el valor en crudo a la vista. Si alguna constante está
/// equivocada, el peor caso es reportar un fallo con su código —honesto y diagnosticable— y nunca un
/// éxito falso.</para>
/// </remarks>
public static class WingetResultParser
{
    // Los códigos viven en WingetErrorCodes: dos de los que había acá estaban mal y produjeron
    // fallos falsos en la primera prueba real. Ver la nota de esa clase.

    /// <param name="packageId">Qué paquete.</param>
    /// <param name="process">Resultado del proceso.</param>
    /// <param name="table">
    /// Tabla de códigos del winget del equipo, si se pudo cargar. Cuando está, su símbolo y
    /// descripción se usan para el detalle: el mapeo lo provee winget y no una constante nuestra.
    /// </param>
    public static WingetResult Parse(string packageId, ProcessResult process, WingetErrorTable? table = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(process);

        if (process.TimedOut)
        {
            return new WingetResult(
                packageId,
                WingetOutcome.TimedOut,
                $"La instalación de {packageId} se canceló por exceder el tiempo límite " +
                $"({process.Duration.TotalMinutes:0} min). El instalador puede haber quedado a medias.",
                null);
        }

        int? code = process.ExitCode;

        if (code == WingetErrorCodes.Success)
        {
            // winget devuelve 0 y avisa por stdout cuando ya estaba instalado y no hizo nada.
            return LooksAlreadyInstalled(process.StandardOutput)
                ? new WingetResult(packageId, WingetOutcome.AlreadyInstalled,
                    $"{packageId} ya estaba instalado.", code)
                : new WingetResult(packageId, WingetOutcome.Installed,
                    $"{packageId} se instaló correctamente.", code);
        }

        if (code is not int value)
        {
            return new WingetResult(packageId, WingetOutcome.Failed,
                $"winget terminó sin devolver un código de salida para {packageId}.", null);
        }

        // "Ya está instalado" NO es un fallo, y era el caso que se reportaba como "no encontrado".
        //
        // Pero es el ÚNICO camino por el que un código de error puede terminar contado como éxito, así
        // que exige confirmación independiente: la tabla del winget del equipo, o que winget lo diga
        // por texto. Una constante nuestra equivocada acá no produciría un fallo falso —visible— sino
        // un éxito falso, que no deja rastro. Ver WingetErrorCodes.ConfirmsAlreadyInstalled.
        if (WingetErrorCodes.ConfirmsAlreadyInstalled(value, table, process.StandardOutput))
        {
            return new WingetResult(packageId, WingetOutcome.AlreadyInstalled,
                $"{packageId} ya estaba instalado en su última versión.", code);
        }

        if (value == WingetErrorCodes.NoApplicationsFound)
        {
            return new WingetResult(packageId, WingetOutcome.NotInCatalog,
                $"El catálogo de winget no tiene ningún paquete con el ID '{packageId}'. Puede haber " +
                "cambiado de nombre, o haber sido removido del repositorio. Hay que corregir el ID en " +
                "appsettings.json, o configurarle una descarga directa.", code);
        }

        if (WingetErrorCodes.IsSourceProblem(value))
        {
            // Este mensaje solo se muestra con la conexión ya confirmada: WingetService comprueba
            // internet antes de lanzar nada. Sin esa comprobación previa, el mismo código aparecía en
            // un equipo sin red y este texto mandaba a buscar un problema de permisos inexistente.
            return new WingetResult(packageId, WingetOutcome.SourceUnavailable,
                "El equipo tiene internet, pero winget no puede leer su catálogo de paquetes. " +
                "Pasa al ejecutarse como administrador: el catálogo se instala por usuario y la " +
                "sesión elevada no lo tiene. EasyFix intenta repararlo solo con " +
                "«winget source reset --force»; si sigue fallando, abrí una consola SIN " +
                "administrador y corré ese mismo comando.", code);
        }

        if (value == WingetErrorCodes.InstallerHashMismatch)
        {
            return new WingetResult(packageId, WingetOutcome.Failed,
                $"El instalador de {packageId} se descargó dañado: su hash no coincide con el del " +
                "catálogo. Casi siempre es una descarga cortada y se resuelve reintentando. Si vuelve " +
                "a pasar, el catálogo de winget está desactualizado respecto del instalador que " +
                "publica el fabricante.", code);
        }

        if (value == WingetErrorCodes.NoApplicableInstaller)
        {
            return new WingetResult(packageId, WingetOutcome.NoApplicableInstaller,
                $"{packageId} no tiene un instalador compatible con este equipo (arquitectura o " +
                "versión de Windows).", code);
        }

        // Cualquier otra cosa: fallo, con todo lo que se pueda decir del código.
        string hex = $"0x{value:X8}";
        string? symbol = table?.SymbolFor(value);
        string? official = table?.DescriptionFor(value);

        string? errorLine = FirstMeaningfulLine(process.StandardError)
                            ?? FirstMeaningfulLine(process.StandardOutput);

        var detail = new System.Text.StringBuilder();
        detail.Append($"Falló la instalación de {packageId} (código {hex}");
        if (symbol is not null) { detail.Append($", {symbol}"); }
        detail.Append(").");
        if (official is { Length: > 0 }) { detail.Append($" {official}."); }
        if (errorLine is not null) { detail.Append($" {errorLine}"); }

        // El código coincide con una constante de «ya estaba instalado» pero nada lo confirmó. Se
        // reporta como fallo —es lo seguro— y se dice por qué, así el técnico puede comprobarlo a mano
        // en vez de quedarse con un código sin explicación.
        if (WingetErrorCodes.LooksAlreadyInstalled(value))
        {
            detail.Append(
                " Este código podría significar que el programa ya estaba instalado, pero no se pudo " +
                "confirmar, así que se informa como fallo en vez de darlo por bueno. Comprobalo en " +
                "«Aplicaciones instaladas».");
        }

        return new WingetResult(packageId, WingetOutcome.Failed, detail.ToString(), code);
    }

    /// <summary>Resultado para cuando winget no está en el equipo.</summary>
    public static WingetResult Missing(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        return new WingetResult(
            packageId,
            WingetOutcome.WingetMissing,
            "winget no está disponible en este equipo. Viene con Windows 10 1809 o superior; " +
            "en versiones anteriores hay que instalar el Instalador de aplicaciones.",
            null);
    }

    /// <summary>
    /// Resultado para cuando el equipo no tiene internet.
    /// </summary>
    /// <remarks>
    /// Se devuelve <b>sin lanzar nada</b>. Todo lo que instala EasyFix se descarga en el momento —no
    /// hay ningún instalador empaquetado dentro del <c>.exe</c>—, así que sin conexión no hay nada
    /// que intentar. Ejecutar winget igual solo produce un error engañoso: en el equipo del
    /// 2026-08-29 devolvió «catálogo no disponible» tres veces y mandó a buscar un problema de
    /// permisos que no existía.
    /// </remarks>
    public static WingetResult Offline(string packageId, ConnectivityResult connectivity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(connectivity);

        return new WingetResult(
            packageId,
            WingetOutcome.NoNetwork,
            $"No se instaló porque el equipo no tiene internet. {connectivity.Detail}",
            null);
    }

    /// <summary>Resumen para el reporte: qué quedó instalado y qué no.</summary>
    public static string Summarize(IReadOnlyList<WingetResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return "No se seleccionó ningún programa.";
        }

        int installed = results.Count(r => r.Outcome == WingetOutcome.Installed);
        int already = results.Count(r => r.Outcome == WingetOutcome.AlreadyInstalled);

        // Sin internet no es «con error»: no se intentó nada. Decir «5 con error» sobre un equipo
        // desconectado manda al técnico a revisar winget en vez de revisar el cable.
        int offline = results.Count(r => r.Outcome == WingetOutcome.NoNetwork);
        int failed = results.Count(r => !r.PackageAvailable && r.Outcome != WingetOutcome.NoNetwork);

        if (offline == results.Count)
        {
            return "No se instaló nada: el equipo no tiene internet. " +
                   "EasyFix descarga todo en el momento, así que sin conexión no hay nada que instalar.";
        }

        var parts = new List<string>();
        if (installed > 0) { parts.Add($"{installed} instalado(s)"); }
        if (already > 0) { parts.Add($"{already} ya estaba(n) instalado(s)"); }
        if (offline > 0) { parts.Add($"{offline} sin internet"); }
        if (failed > 0) { parts.Add($"{failed} con error"); }

        return string.Join(", ", parts) + '.';
    }

    private static bool LooksAlreadyInstalled(string stdout) =>
        stdout.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("ya está instalado", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Primera línea con contenido real de un texto.
    /// </summary>
    /// <remarks>
    /// winget escribe caracteres de animación de progreso (<c>-</c>, <c>\</c>, <c>|</c>, <c>/</c>,
    /// bloques) en su salida. En un equipo real el detalle de un fallo terminó siendo literalmente
    /// «-», que no le dice nada a nadie. Esas líneas se descartan.
    /// </remarks>
    private static string? FirstMeaningfulLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();

            // Se descartan las líneas que son SOLO animación de progreso, sin recortar el contenido
            // de las que sí tienen mensaje: un "Access is denied." no debe perder su punto final.
            if (trimmed.Length == 0 || IsProgressNoise(trimmed))
            {
                continue;
            }

            return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
        }

        return null;
    }

    /// <summary>
    /// <c>true</c> si la línea solo tiene caracteres de animación de progreso.
    /// </summary>
    /// <remarks>
    /// winget dibuja un spinner y una barra de progreso en stdout. En un equipo real el detalle de un
    /// fallo terminó siendo literalmente «-», que no le dice nada a nadie.
    /// </remarks>
    private static bool IsProgressNoise(string line)
    {
        const string NoiseChars = @"-\|/█▒░.  ";

        foreach (char c in line)
        {
            if (!NoiseChars.Contains(c, StringComparison.Ordinal) && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
