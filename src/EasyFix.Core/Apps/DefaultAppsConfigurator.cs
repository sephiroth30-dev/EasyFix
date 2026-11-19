using System.Runtime.Versioning;
using System.Text;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Processes;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Apps;

/// <summary>Qué programa está puesto hoy como predeterminado para algo.</summary>
/// <param name="IsExpected"><c>true</c> si ya es el que queremos. <c>null</c> = no se pudo leer.</param>
/// <param name="CurrentProgId">Lo que hay configurado ahora, para el reporte. <c>null</c> si no hay.</param>
public sealed record DefaultAppStatus(bool? IsExpected, string? CurrentProgId);

/// <summary>Qué pasó al intentar dejar Chrome y Acrobat Reader como predeterminados.</summary>
/// <param name="AppliedToNewProfiles">
/// <c>true</c> si DISM aceptó el archivo de asociaciones. <b>Solo afecta a perfiles de usuario
/// creados DESPUÉS</b> — es el alcance que Windows permite por esta vía, no una limitación de
/// la implementación.
/// </param>
/// <param name="Detail">Qué pasó, en español, para el reporte.</param>
public sealed record DefaultAppsResult(bool AppliedToNewProfiles, string Detail);

/// <summary>
/// Deja Chrome como navegador y Acrobat Reader para los PDF, por la vía oficial de Microsoft.
/// </summary>
/// <remarks>
/// <para><b>Lo que Windows NO permite, y por qué esto no lo intenta.</b> Cambiar el predeterminado
/// del usuario que está logueado está bloqueado a propósito: el valor de <c>UserChoice</c> va
/// firmado con un hash por usuario+equipo que Microsoft no documenta, y desde febrero de 2024 el
/// driver <c>UCPD.sys</c> impide escribir esas claves aunque se tenga el hash. Las herramientas que
/// lo logran igual necesitan <b>desactivar ese driver</b>, que es exactamente lo que el «Tier C — NO
/// construir» de <c>docs/PLAN.md</c> prohíbe: pelearse con una protección del sistema, frágil ante
/// cada actualización grande, y de paso hacer que EasyFix se parezca a lo que un antivirus marca.</para>
///
/// <para><b>Lo que sí hace, y es la vía sancionada por Microsoft.</b>
/// <c>DISM /Online /Import-DefaultAppAssociations</c> es el mecanismo documentado para imágenes
/// corporativas y OEM. Funciona en todas las ediciones, incluida Home, sin desactivar nada. Su
/// alcance real: <b>los perfiles de usuario que se creen después</b>. Para un técnico preparando un
/// equipo antes de entregarlo —o que crea un perfil nuevo para el cliente— es justo lo que hace
/// falta; para el perfil que ya está abierto, no hay vía automática que no implique romper la
/// protección.</para>
///
/// <para><b>Los ProgId están SIN VERIFICAR en un equipo real.</b> <c>ChromeHTML</c> y
/// <c>AcroExch.Document.DC</c> son los que registran esos instaladores según su documentación, pero
/// nunca se los leyó del registro de un equipo con ambos instalados. Si un ProgId no existe en el
/// equipo, Windows ignora esa asociación en silencio — falla hacia «no pasó nada», no hacia una
/// asociación rota, que es la dirección correcta para equivocarse.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DefaultAppsConfigurator
{
    /// <summary>ProgId que registra el instalador de Chrome. SIN VERIFICAR contra un equipo real.</summary>
    private const string ChromeProgId = "ChromeHTML";

    /// <summary>ProgId de Acrobat Reader DC. SIN VERIFICAR contra un equipo real.</summary>
    private const string AcrobatProgId = "AcroExch.Document.DC";

    private readonly IProcessRunner _runner;
    private readonly ILogger<DefaultAppsConfigurator> _logger;

    public DefaultAppsConfigurator(IProcessRunner runner, ILogger<DefaultAppsConfigurator> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// Qué navegador está puesto como predeterminado para <c>http</c> en el perfil actual.
    /// </summary>
    /// <remarks>
    /// <para><b>Leer sí se puede; escribir no.</b> Windows protege la escritura de
    /// <c>UserChoice</c> (hash + driver <c>UCPD.sys</c>), pero el valor es legible sin ningún
    /// truco. Eso permite lo que de verdad hace falta: <b>saber si hace falta hacer algo</b>, en
    /// vez de abrir la ventana de Configuración a ciegas cada vez.</para>
    ///
    /// <para><c>null</c> en <see cref="DefaultAppStatus.IsExpected"/> significa que no se pudo
    /// leer — no se afirma ni que está ni que no está.</para>
    /// </remarks>
    public DefaultAppStatus GetBrowserStatus() =>
        ReadUserChoice(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Shell Associations\UrlAssociations\http\UserChoice",
            ChromeProgId);

    /// <summary>Qué programa abre los PDF en el perfil actual.</summary>
    public DefaultAppStatus GetPdfStatus() =>
        ReadUserChoice(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice",
            AcrobatProgId);

    private DefaultAppStatus ReadUserChoice(string keyPath, string expectedProgId)
    {
        try
        {
            if (Microsoft.Win32.Registry.GetValue(keyPath, "ProgId", null) is not string progId ||
                progId.Length == 0)
            {
                // La clave no existe: nunca se eligió nada explícitamente. No es «no es el
                // nuestro», es «no hay dato» — y la diferencia importa para no afirmar de más.
                return new DefaultAppStatus(null, null);
            }

            // Comparación por prefijo: Acrobat registra variantes («AcroExch.Document.DC»,
            // «Acrobat.Document.DC» según versión) y Chrome agrega un sufijo por instalación
            // en algunos equipos.
            bool matches = progId.StartsWith(expectedProgId, StringComparison.OrdinalIgnoreCase) ||
                           expectedProgId.StartsWith(progId, StringComparison.OrdinalIgnoreCase);

            return new DefaultAppStatus(matches, progId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el predeterminado en {Key}.", keyPath);
            return new DefaultAppStatus(null, null);
        }
    }

    /// <summary>
    /// Escribe el archivo de asociaciones y se lo pasa a DISM. No pregunta nada: se invoca después
    /// de instalar, cuando el técnico ya decidió que quiere esos programas en el equipo.
    /// </summary>
    /// <param name="setChrome">Chrome se instaló (o ya estaba) y conviene asociarlo.</param>
    /// <param name="setAcrobat">Acrobat Reader se instaló (o ya estaba).</param>
    public async Task<DefaultAppsResult> ApplyAsync(
        bool setChrome, bool setAcrobat, CancellationToken ct)
    {
        if (!setChrome && !setAcrobat)
        {
            return new DefaultAppsResult(false, "No había nada que asociar.");
        }

        string xml = BuildAssociationsXml(setChrome, setAcrobat);
        string path = Path.Combine(Path.GetTempPath(), "EasyFix-DefaultAssociations.xml");

        try
        {
            await File.WriteAllTextAsync(path, xml, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo escribir el archivo de asociaciones.");
            return new DefaultAppsResult(false,
                $"No se pudo preparar la configuración de predeterminados: {ex.Message}");
        }

        try
        {
            ProcessResult result = await _runner.RunAsync(
                SafeProcessRunner.System32("Dism.exe"),
                new[] { "/Online", "/English", $"/Import-DefaultAppAssociations:{path}" },
                TimeSpan.FromMinutes(3),
                ct).ConfigureAwait(false);

            var programs = new List<string>();
            if (setChrome) { programs.Add("Google Chrome como navegador"); }
            if (setAcrobat) { programs.Add("Acrobat Reader para los PDF"); }
            string what = string.Join(" y ", programs);

            if (result.Succeeded)
            {
                _logger.LogInformation("Asociaciones predeterminadas importadas: {What}.", what);

                return new DefaultAppsResult(true,
                    $"Se configuró {what} para los usuarios NUEVOS de este equipo. " +
                    "Para el usuario que está abierto ahora, Windows no permite que un programa " +
                    "lo cambie (lo bloquea desde 2024): usá el botón «Predeterminados» y marcalo " +
                    "a mano, son dos clics.");
            }

            _logger.LogWarning(
                "DISM rechazó las asociaciones (código {Code}): {Error}",
                result.ExitCode, result.StandardError);

            return new DefaultAppsResult(false,
                $"No se pudieron configurar los predeterminados (DISM devolvió {result.ExitCode}). " +
                "Se pueden poner a mano con el botón «Predeterminados».");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la importación de asociaciones predeterminadas.");
            return new DefaultAppsResult(false,
                $"No se pudieron configurar los predeterminados: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un XML de dos líneas en %TEMP% no justifica reintentar.
            }
        }
    }

    /// <summary>
    /// El formato que espera DISM. Es el mismo que produce
    /// <c>Dism /Online /Export-DefaultAppAssociations</c>, así que se puede comparar contra la
    /// salida real de un equipo para auditarlo.
    /// </summary>
    private static string BuildAssociationsXml(bool setChrome, bool setAcrobat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("<DefaultAssociations>");

        if (setChrome)
        {
            // Los cuatro que hacen que "abrir un link" use Chrome: los dos protocolos y las dos
            // extensiones de página. Con http/https solos, un .html del escritorio seguiría
            // abriendo en Edge.
            foreach (string id in new[] { "http", "https", ".htm", ".html" })
            {
                sb.AppendLine(Association(id, ChromeProgId, "Google Chrome"));
            }
        }

        if (setAcrobat)
        {
            sb.AppendLine(Association(".pdf", AcrobatProgId, "Adobe Acrobat Reader"));
        }

        sb.AppendLine("</DefaultAssociations>");
        return sb.ToString();

        static string Association(string identifier, string progId, string appName) =>
            $"""  <Association Identifier="{identifier}" ProgId="{progId}" ApplicationName="{appName}" />""";
    }
}
