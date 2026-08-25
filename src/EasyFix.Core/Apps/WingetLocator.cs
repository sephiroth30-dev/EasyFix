using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Apps;

/// <summary>Encuentra el ejecutable de winget en el equipo.</summary>
public interface IWingetLocator
{
    /// <summary>Ruta absoluta a <c>winget.exe</c>, o <c>null</c> si no está disponible.</summary>
    string? Find();
}

/// <summary>
/// Busca <c>winget.exe</c> en el orden que funciona para un proceso elevado.
/// </summary>
/// <remarks>
/// <para><b>El problema que resuelve.</b> winget se instala como paquete MSIX <i>por usuario</i>, y se
/// invoca normalmente por un alias en <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>. EasyFix corre
/// elevado, y al elevar el proceso <c>%LOCALAPPDATA%</c> puede resolver a otro perfil —el del
/// administrador— donde ese alias no existe. Buscar solo ahí falla justo en el escenario real.</para>
///
/// <para>Por eso se busca primero la <b>instalación real del paquete</b> bajo
/// <c>C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*</c>, que es global y accesible
/// para un proceso elevado (la carpeta tiene ACL restrictiva, pero un administrador puede leerla). El
/// alias por usuario queda como respaldo.</para>
///
/// <para>Nunca se resuelve por <c>PATH</c>: eso permitiría que un <c>winget.exe</c> puesto en el
/// directorio actual —en el equipo comprometido que estamos reparando— secuestre la instalación.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WingetLocator : IWingetLocator
{
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";

    private readonly ILogger<WingetLocator> _logger;

    public WingetLocator(ILogger<WingetLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string? Find()
    {
        foreach (string candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("winget encontrado en {Path}.", candidate);
                return candidate;
            }
        }

        _logger.LogWarning("No se encontró winget.exe en este equipo.");
        return null;
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        // 1) La instalación real del paquete: global, sirve elevado.
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

        if (Directory.Exists(windowsApps))
        {
            IEnumerable<string> packageDirs;
            try
            {
                packageDirs = Directory.EnumerateDirectories(windowsApps, PackagePrefix + "*")
                    // Puede haber varias versiones instaladas; la más nueva ordena última por nombre.
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "No se pudo enumerar {Path}.", windowsApps);
                packageDirs = Array.Empty<string>();
            }

            foreach (string dir in packageDirs)
            {
                yield return Path.Combine(dir, "winget.exe");
            }
        }

        // 2) El alias por usuario, que puede no existir bajo el perfil del administrador.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
    }
}
