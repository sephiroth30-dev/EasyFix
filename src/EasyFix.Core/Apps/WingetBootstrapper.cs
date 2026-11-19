using System.Runtime.Versioning;
using EasyFix.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Apps;

/// <summary>Resultado de intentar instalar winget cuando falta.</summary>
/// <param name="Success">
/// <c>true</c> solo si, después de instalar, <c>winget.exe</c> se pudo volver a encontrar. Correr el
/// instalador sin tirar error no es la prueba: la única prueba es que el reintento funcione — mismo
/// principio que ya aplica la reparación de fuentes de winget.
/// </param>
/// <param name="Detail">Qué pasó, en español, para el reporte y el log.</param>
public sealed record WingetBootstrapResult(bool Success, string Detail);

public interface IWingetBootstrapper
{
    /// <summary>
    /// Si winget ya está, no hace nada y devuelve éxito. Si falta, intenta instalarlo. Requiere
    /// internet: quien llama ya tiene que haber pasado la compuerta de red.
    /// </summary>
    Task<WingetBootstrapResult> EnsureAvailableAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Instala el «Instalador de aplicaciones» (el paquete que trae <c>winget.exe</c>) cuando no está.
/// </summary>
/// <remarks>
/// <para><b>Por qué hace falta.</b> winget viene con Windows desde la versión 1809, pero varios
/// escenarios reales lo dejan afuera igual: imágenes de fábrica debloateadas que sacan las apps de
/// Store, cuentas locales que nunca abrieron la Store (así nunca se aprovisiona el paquete por
/// usuario), ediciones N/Server, o Windows 10 anterior a 1809. El técnico se encontraba con
/// «winget.exe NO se encontró» y ahí se terminaba: el botón «Diagnosticar» solo explica el problema,
/// no lo resuelve.</para>
///
/// <para><b>Qué instala y de dónde, siempre origen oficial:</b></para>
/// <list type="bullet">
/// <item><c>Microsoft.DesktopAppInstaller_*.msixbundle</c> — el último release de
/// <c>microsoft/winget-cli</c> en GitHub, el repositorio oficial del propio winget. Se resuelve por
/// la API de releases, nunca una versión clavada.</item>
/// <item><c>Microsoft.VCLibs.x64.14.00.Desktop.appx</c> — redirect estable de Microsoft
/// (<c>aka.ms</c>), la dependencia que falta en la enorme mayoría de los casos reales. Es el mismo
/// enlace que documenta Microsoft en sus propias guías de solución de problemas de winget.</item>
/// </list>
///
/// <para><b>Lo que esto NO cubre, a propósito — SIN VERIFICAR, documentado en vez de adivinado.</b>
/// Algunos equipos además necesitan <c>Microsoft.UI.Xaml</c> como dependencia, y no hay un enlace
/// oficial estable para bajar ese paquete suelto: la vía oficial es la Microsoft Store o extraerlo de
/// un paquete NuGet, y las dos son frágiles para automatizar a ciegas. Si <c>Add-AppxPackage</c>
/// falla por esa dependencia, el detalle del resultado lo dice tal cual lo reporta Windows, en vez de
/// inventar una segunda descarga que nadie probó.</para>
///
/// <para><b>Por qué PowerShell y no WinRT.</b> Instalar paquetes MSIX desde .NET «como corresponde»
/// es <c>Windows.Management.Deployment.PackageManager</c>, pero eso exige la superficie de CsWinRT /
/// Windows SDK projections — justo la clase de dependencia nueva que el proyecto evita a propósito
/// (ver «Desviaciones conscientes» en <c>docs/PLAN.md</c>). <c>Add-AppxPackage</c> es el cmdlet que
/// hace lo mismo, invocado igual que DISM o netsh: ruta absoluta, <c>ArgumentList</c>, sin
/// <c>cmd.exe</c>.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WingetBootstrapper : IWingetBootstrapper
{
    private const string WingetCliRepository = "microsoft/winget-cli";
    private const string BundleAssetPattern = ".msixbundle";

    private static readonly Uri VCLibsUrl =
        new("https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx");

    private readonly IFileDownloader _downloader;
    private readonly IProcessRunner _runner;
    private readonly IWingetLocator _locator;
    private readonly ILogger<WingetBootstrapper> _logger;

    public WingetBootstrapper(
        IFileDownloader downloader,
        IProcessRunner runner,
        IWingetLocator locator,
        ILogger<WingetBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(logger);

        _downloader = downloader;
        _runner = runner;
        _locator = locator;
        _logger = logger;
    }

    public async Task<WingetBootstrapResult> EnsureAvailableAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        if (_locator.Find() is not null)
        {
            return new WingetBootstrapResult(true, "winget ya estaba disponible.");
        }

        _logger.LogInformation("winget no está. Intentando instalarlo automáticamente.");
        progress?.Report("winget no está instalado. Descargándolo desde Microsoft…");

        Uri? bundleUrl;
        try
        {
            bundleUrl = await _downloader
                .ResolveGitHubLatestAsync(WingetCliRepository, BundleAssetPattern, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "No se pudo consultar el último release de {Repo}.", WingetCliRepository);
            return new WingetBootstrapResult(false,
                $"No se pudo averiguar cuál es la última versión de winget: {ex.Message}");
        }

        if (bundleUrl is null)
        {
            return new WingetBootstrapResult(false,
                "No se encontró el instalador de winget en su repositorio oficial de GitHub. " +
                "Puede haber cambiado el nombre del archivo del release.");
        }

        string bundlePath, vclibsPath;
        try
        {
            progress?.Report("Descargando el Instalador de aplicaciones…");
            bundlePath = await _downloader
                .DownloadAsync(bundleUrl, "EasyFix-DesktopAppInstaller.msixbundle", progress, ct)
                .ConfigureAwait(false);

            progress?.Report("Descargando una dependencia necesaria (VCLibs)…");
            vclibsPath = await _downloader
                .DownloadAsync(VCLibsUrl, "EasyFix-VCLibs.appx", progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "No se pudo descargar winget o su dependencia.");
            return new WingetBootstrapResult(false, $"No se pudo descargar winget: {ex.Message}");
        }

        try
        {
            progress?.Report("Instalando winget…");

            ProcessResult result;
            try
            {
                result = await RunAddAppxPackageAsync(bundlePath, vclibsPath, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Si powershell.exe ni siquiera arranca (bloqueado por AppLocker/WDAC/EDR en un
                // equipo corporativo gestionado, el escenario más plausible), esto tira una
                // excepción real en vez de un ProcessResult fallido. Antes escapaba sin capturar y
                // abortaba TODA la tanda de instalación — un técnico queriendo instalar cuatro
                // programas se quedaba sin ninguno por un fallo que solo afectaba a winget.
                _logger.LogError(ex, "No se pudo ejecutar Add-AppxPackage.");
                return new WingetBootstrapResult(false,
                    $"No se pudo ejecutar el instalador de winget: {ex.Message}");
            }

            // La única prueba de que funcionó es que winget aparezca después. Que Add-AppxPackage
            // devuelva código 0 no alcanza: mismo principio que la reparación de fuentes, que una vez
            // "funcionó" sin haber reparado nada.
            string? installed = _locator.Find();

            if (installed is not null)
            {
                _logger.LogInformation("winget instalado automáticamente en {Path}.", installed);
                return new WingetBootstrapResult(true,
                    "winget se instaló automáticamente y ya está disponible.");
            }

            string detail = result.TimedOut
                ? "La instalación de winget no terminó a tiempo."
                : BuildFailureDetail(result);

            _logger.LogWarning(
                "Add-AppxPackage corrió (código {Code}) pero winget sigue sin encontrarse. {Detail}",
                result.ExitCode, detail);

            return new WingetBootstrapResult(false, detail);
        }
        finally
        {
            TryDelete(bundlePath);
            TryDelete(vclibsPath);
        }
    }

    /// <summary>
    /// Arma y corre el único <c>powershell.exe</c> que necesita <c>Add-AppxPackage</c>.
    /// </summary>
    /// <remarks>
    /// El script va como UN argumento de <c>-Command</c>: <c>ArgumentList</c> lo pasa entero sin que
    /// <c>cmd.exe</c> lo toque (<c>UseShellExecute = false</c>), así que no hay injection por el lado
    /// del proceso. Dentro del script, las rutas van en comillas simples de PowerShell con el escape
    /// de comilla duplicada — defensivo: nuestros nombres de archivo son fijos y no la necesitan, pero
    /// cuesta cero hacerlo bien.
    /// </remarks>
    private Task<ProcessResult> RunAddAppxPackageAsync(string bundlePath, string vclibsPath, CancellationToken ct)
    {
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        string script =
            "$ErrorActionPreference = 'Stop'; " +
            $"Add-AppxPackage -Path {PsQuote(bundlePath)} -DependencyPath @({PsQuote(vclibsPath)})";

        return _runner.RunAsync(
            powershell,
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            TimeSpan.FromMinutes(3),
            ct);
    }

    private static string PsQuote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>El error real de Appx está casi siempre en stderr; stdout suele venir vacío.</summary>
    private static string BuildFailureDetail(ProcessResult result)
    {
        string raw = result.StandardError.Trim().Length > 0
            ? result.StandardError.Trim()
            : result.StandardOutput.Trim();

        string message = raw.Length > 0
            ? raw
            : $"Add-AppxPackage devolvió el código {result.ExitCode} sin más detalle.";

        // El caso documentado y SIN VERIFICAR: falta Microsoft.UI.Xaml y no se intenta resolver
        // solo. Solo el nombre de la dependencia, que Windows sí imprime en el mensaje real de
        // Add-AppxPackage cuando falta — un código numérico que nadie confirmó en un equipo real
        // estuvo acá antes y se sacó: un heurístico sin fuente puede dar tanto falso positivo como
        // falso negativo, y encima el texto de abajo afirmaría una causa que no se verificó.
        if (message.Contains("Xaml", StringComparison.OrdinalIgnoreCase))
        {
            return "No se pudo instalar winget: falta una dependencia adicional " +
                   "(probablemente Microsoft.UI.Xaml) que este equipo no tiene y que no se puede " +
                   $"bajar de un origen oficial estable. Detalle de Windows: {message}";
        }

        return $"No se pudo instalar winget: {message}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No queda nada crítico en el disco del cliente por esto; no vale la pena reintentar.
        }
    }
}
