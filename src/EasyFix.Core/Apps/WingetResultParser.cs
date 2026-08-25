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

    /// <summary>El paquete no está en el repositorio de winget. Hace falta descarga directa.</summary>
    NotInRepository,

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
    /// <summary>Éxito.</summary>
    public const int Success = 0;

    /// <summary><c>APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND</c> (pendiente de verificar).</summary>
    public static readonly int NoApplicationsFound = unchecked((int)0x8A15002B);

    /// <summary><c>APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED</c> (pendiente de verificar).</summary>
    public static readonly int PackageAlreadyInstalled = unchecked((int)0x8A150056);

    /// <summary><c>APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER</c> (pendiente de verificar).</summary>
    public static readonly int NoApplicableInstaller = unchecked((int)0x8A150061);

    /// <summary>
    /// <c>APPINSTALLER_CLI_ERROR_SOURCE_DATA_MISSING</c>: falta la metadata de la fuente.
    /// </summary>
    /// <remarks>
    /// Es el error que aparece al correr winget elevado. El paquete <c>Microsoft.Winget.Source</c> se
    /// instala por usuario y la sesión del administrador no lo tiene, así que winget queda sin
    /// catálogo. Ver microsoft/winget-cli#698. Se remedia con <c>winget source reset --force</c>.
    /// </remarks>
    public static readonly int SourceDataMissing = unchecked((int)0x8A15000F);

    /// <summary><c>APPINSTALLER_CLI_ERROR_FAILED_TO_OPEN_ALL_SOURCES</c>.</summary>
    public static readonly int FailedToOpenAllSources = unchecked((int)0x8A150014);

    /// <summary><c>APPINSTALLER_CLI_ERROR_SOURCE_NOT_FOUND</c>.</summary>
    public static readonly int SourceNotFound = unchecked((int)0x8A150010);

    public static WingetResult Parse(string packageId, ProcessResult process)
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

        if (code == Success)
        {
            // winget devuelve 0 y avisa por stdout cuando ya estaba instalado y no hizo nada.
            return LooksAlreadyInstalled(process.StandardOutput)
                ? new WingetResult(packageId, WingetOutcome.AlreadyInstalled,
                    $"{packageId} ya estaba instalado.", code)
                : new WingetResult(packageId, WingetOutcome.Installed,
                    $"{packageId} se instaló correctamente.", code);
        }

        if (code == PackageAlreadyInstalled)
        {
            return new WingetResult(packageId, WingetOutcome.AlreadyInstalled,
                $"{packageId} ya estaba instalado.", code);
        }

        if (code == NoApplicationsFound)
        {
            return new WingetResult(packageId, WingetOutcome.NotFound,
                $"No se encontró el paquete '{packageId}'. Puede haber cambiado de ID en el " +
                "repositorio: hay que actualizarlo en appsettings.json.", code);
        }

        if (code == SourceDataMissing || code == FailedToOpenAllSources || code == SourceNotFound)
        {
            return new WingetResult(packageId, WingetOutcome.SourceUnavailable,
                "winget no puede leer su catálogo de paquetes. Pasa al ejecutarse como " +
                "administrador: el catálogo se instala por usuario y la sesión elevada no lo tiene. " +
                "EasyFix intenta repararlo solo con «winget source reset --force»; si sigue " +
                "fallando, abrí una consola SIN administrador y corré ese mismo comando.", code);
        }

        if (code == NoApplicableInstaller)
        {
            return new WingetResult(packageId, WingetOutcome.NoApplicableInstaller,
                $"{packageId} no tiene un instalador compatible con este equipo (arquitectura o " +
                "versión de Windows).", code);
        }

        // Cualquier otra cosa: fallo, con el código en crudo para poder diagnosticarlo.
        string hex = code is null ? "desconocido" : $"0x{code.Value:X8}";

        // stderr primero; si winget no escribió nada ahí, la primera línea útil de stdout suele
        // traer el motivo. Puede no haber ninguna de las dos.
        string? errorLine = FirstMeaningfulLine(process.StandardError)
                            ?? FirstMeaningfulLine(process.StandardOutput);

        return new WingetResult(
            packageId,
            WingetOutcome.Failed,
            $"Falló la instalación de {packageId} (código {hex})." +
            (errorLine is null ? string.Empty : $" {errorLine}"),
            code);
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
