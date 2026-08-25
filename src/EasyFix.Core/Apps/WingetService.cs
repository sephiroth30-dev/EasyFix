using EasyFix.Core.Abstractions;
using EasyFix.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Apps;

/// <param name="PackageId">Qué se está instalando.</param>
/// <param name="DisplayName">Nombre para mostrar.</param>
/// <param name="Index">Cuál de la tanda, empezando en 1.</param>
/// <param name="Total">Cuántos en total.</param>
/// <param name="Finished">Resultado, cuando ya terminó. <c>null</c> mientras está en curso.</param>
public sealed record InstallProgress(
    string PackageId,
    string DisplayName,
    int Index,
    int Total,
    WingetResult? Finished = null);

/// <summary>
/// Instala programas con winget.
/// </summary>
/// <remarks>
/// <para><b>Todo se descarga en el momento.</b> winget baja cada paquete del repositorio oficial de
/// Microsoft en el instante de instalar, así que siempre entra la última versión publicada. EasyFix no
/// empaqueta ningún instalador: no hay nada que envejezca dentro del <c>.exe</c>, y no hay que
/// regenerarlo cuando Chrome saque una versión nueva. Requiere que el equipo tenga internet.</para>
///
/// <para><b>En serie, nunca en paralelo.</b> Los instaladores de Windows se pelean por el mutex de
/// Windows Installer: dos a la vez terminan con uno fallando o, peor, a medias.</para>
///
/// <para><b>Cada resultado se interpreta.</b> Ver <see cref="WingetResultParser"/>: "ya estaba
/// instalado" no es un fallo, y un código desconocido se reporta con su valor en crudo en vez de
/// pasar por éxito.</para>
/// </remarks>
public sealed class WingetService
{
    private readonly IProcessRunner _runner;
    private readonly IWingetLocator _locator;
    private readonly DirectDownloadInstaller _directInstaller;
    private readonly ThresholdOptions _thresholds;
    private readonly ILogger<WingetService> _logger;

    /// <summary>Se intenta reparar las fuentes una sola vez por corrida.</summary>
    private bool _sourceRepairAttempted;

    public WingetService(
        IProcessRunner runner,
        IWingetLocator locator,
        DirectDownloadInstaller directInstaller,
        ThresholdOptions thresholds,
        ILogger<WingetService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(directInstaller);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _locator = locator;
        _directInstaller = directInstaller;
        _thresholds = thresholds;
        _logger = logger;
    }

    /// <summary><c>true</c> si winget está disponible en este equipo.</summary>
    public bool IsAvailable => _locator.Find() is not null;

    /// <summary>Versión de winget, o <c>null</c> si no está o no responde.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        string? winget = _locator.Find();
        if (winget is null)
        {
            return null;
        }

        ProcessResult result = await _runner
            .RunAsync(winget, new[] { "--version" }, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    /// <summary>
    /// Instala los paquetes indicados, uno después del otro. No tira excepción por un paquete que
    /// falla: devuelve un resultado por cada uno.
    /// </summary>
    public async Task<IReadOnlyList<WingetResult>> InstallAsync(
        IReadOnlyList<WingetPackage> packages,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var results = new List<WingetResult>(packages.Count);

        _sourceRepairAttempted = false;
        string? winget = _locator.Find();

        for (int i = 0; i < packages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            WingetPackage package = packages[i];
            progress?.Report(new InstallProgress(package.Id, package.Name, i + 1, packages.Count));

            WingetResult result;

            if (package.Direct is not null)
            {
                // Configurado para bajarse de su origen oficial: no pasa por winget en absoluto.
                var textProgress = new Progress<string>(_ => { });
                result = await _directInstaller
                    .InstallAsync(package, textProgress, ct)
                    .ConfigureAwait(false);
            }
            else if (winget is null)
            {
                result = WingetResultParser.Missing(package.Id);
            }
            else
            {
                result = await InstallOneAsync(winget, package, ct).ConfigureAwait(false);

                // Reparar las fuentes y reintentar una vez. Es el fallo más común al correr elevado:
                // el catálogo de winget se instala por usuario y la sesión de administrador no lo ve.
                if (result.Outcome == WingetOutcome.SourceUnavailable && !_sourceRepairAttempted)
                {
                    _sourceRepairAttempted = true;

                    if (await TryRepairSourcesAsync(winget, ct).ConfigureAwait(false))
                    {
                        _logger.LogInformation("Fuentes de winget reparadas. Reintentando {Package}.", package.Id);
                        result = await InstallOneAsync(winget, package, ct).ConfigureAwait(false);
                    }
                }
            }

            results.Add(result);

            progress?.Report(new InstallProgress(
                package.Id, package.Name, i + 1, packages.Count, result));
        }

        return results;
    }

    /// <summary>
    /// Intenta reparar el catálogo de winget.
    /// </summary>
    /// <remarks>
    /// El error <c>0x8A15000F</c> aparece al correr winget desde una sesión elevada: el paquete
    /// <c>Microsoft.Winget.Source</c> está instalado para el usuario que inició sesión, no para la
    /// cuenta de administrador con la que corre el proceso elevado, así que winget queda sin
    /// catálogo y toda instalación falla. Ver microsoft/winget-cli#698.
    /// <para><c>source reset --force</c> vuelve a agregar las fuentes por defecto. Después se fuerza
    /// una actualización del catálogo, que es lo que faltaba.</para>
    /// </remarks>
    private async Task<bool> TryRepairSourcesAsync(string wingetPath, CancellationToken ct)
    {
        _logger.LogWarning("winget no puede leer sus fuentes. Intentando «source reset --force».");

        ProcessResult reset = await _runner
            .RunAsync(wingetPath, new[] { "source", "reset", "--force" }, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);

        if (!reset.Succeeded)
        {
            _logger.LogError(
                "«winget source reset --force» falló con {Code}: {Error}",
                reset.ExitCode, reset.StandardError);
            return false;
        }

        ProcessResult update = await _runner
            .RunAsync(wingetPath, new[] { "source", "update" }, TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);

        if (!update.Succeeded)
        {
            _logger.LogWarning(
                "«winget source update» falló con {Code}, pero el reset sí funcionó: se reintenta igual.",
                update.ExitCode);
        }

        return true;
    }

    /// <summary>
    /// Diagnóstico de winget, para cuando falla y hace falta saber por qué. Solo lectura.
    /// </summary>
    public async Task<string> DiagnoseAsync(CancellationToken ct = default)
    {
        string? winget = _locator.Find();
        if (winget is null)
        {
            return "winget.exe NO se encontró. Se buscó en Program Files\\WindowsApps " +
                   "(instalación del paquete) y en el alias de %LOCALAPPDATA%.";
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"winget: {winget}");

        ProcessResult version = await _runner
            .RunAsync(winget, new[] { "--version" }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        report.AppendLine($"versión: {version.StandardOutput.Trim()} (código {version.ExitCode})");

        ProcessResult sources = await _runner
            .RunAsync(winget, new[] { "source", "list" }, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        report.AppendLine($"fuentes (código {sources.ExitCode}):");
        report.AppendLine(sources.StandardOutput.Trim());

        if (sources.StandardError.Trim().Length > 0)
        {
            report.AppendLine("stderr:");
            report.AppendLine(sources.StandardError.Trim());
        }

        return report.ToString();
    }

    private async Task<WingetResult> InstallOneAsync(
        string wingetPath,
        WingetPackage package,
        CancellationToken ct)
    {
        // Validar antes de pasarlo a un proceso, aunque ArgumentList ya impida la inyección: un ID
        // basura tiene que fallar con un mensaje claro y no con un error raro de winget.
        if (!WingetPackageId.IsValid(package.Id))
        {
            _logger.LogError("El ID '{PackageId}' de appsettings.json no es válido.", package.Id);
            return new WingetResult(
                package.Id,
                WingetOutcome.Failed,
                $"El ID '{package.Id}' no es válido. Se aceptan letras, números y . _ + -",
                null);
        }

        string[] arguments =
        {
            "install",
            "--id", package.Id,
            "--exact",                        // sin esto, un ID parcial puede traer otro paquete
            "--source", "winget",             // solo el repositorio oficial, no fuentes agregadas
            "--silent",
            "--disable-interactivity",        // ningún instalador puede quedarse esperando un Enter
            "--accept-package-agreements",
            "--accept-source-agreements",
        };

        _logger.LogInformation("Instalando {PackageId} ({Name}).", package.Id, package.Name);

        try
        {
            ProcessResult process = await _runner
                .RunAsync(wingetPath, arguments, _thresholds.ExternalProcessTimeout, ct)
                .ConfigureAwait(false);

            WingetResult result = WingetResultParser.Parse(package.Id, process);

            _logger.LogInformation(
                "{PackageId}: {Outcome} (código {Code}).", package.Id, result.Outcome, result.ExitCode);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un paquete que revienta no puede cortar la tanda: los demás se siguen instalando.
            _logger.LogError(ex, "Falló la instalación de {PackageId}.", package.Id);
            return new WingetResult(
                package.Id,
                WingetOutcome.Failed,
                $"No se pudo ejecutar winget para {package.Id}: {ex.GetType().Name}: {ex.Message}",
                null);
        }
    }
}
