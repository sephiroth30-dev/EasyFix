using EasyFix.Core.Abstractions;

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

        // "Ya está instalado" NO es un fallo. UPDATE_NOT_APPLICABLE sobre un install significa
        // exactamente eso, y era el caso que se reportaba como "no encontrado".
        if (WingetErrorCodes.MeansAlreadyInstalled(value))
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
            return new WingetResult(packageId, WingetOutcome.SourceUnavailable,
                "winget no puede leer su catálogo de paquetes. Pasa al ejecutarse como " +
                "administrador: el catálogo se instala por usuario y la sesión elevada no lo tiene. " +
                "EasyFix intenta repararlo solo con «winget source reset --force»; si sigue " +
                "fallando, abrí una consola SIN administrador y corré ese mismo comando.", code);
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
        int failed = results.Count(r => !r.PackageAvailable);

        var parts = new List<string>();
        if (installed > 0) { parts.Add($"{installed} instalado(s)"); }
        if (already > 0) { parts.Add($"{already} ya estaba(n) instalado(s)"); }
        if (failed > 0) { parts.Add($"{failed} con error"); }

        return string.Join(", ", parts) + '.';
    }

    private static bool LooksAlreadyInstalled(string stdout) =>
        stdout.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("ya está instalado", StringComparison.OrdinalIgnoreCase) ||
        stdout.Contains("No newer package versions", StringComparison.OrdinalIgnoreCase);

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
            }
        }

        return null;
    }
}
