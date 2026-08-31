using EasyFix.Core.Abstractions;
using EasyFix.Core.Configuration;
using EasyFix.Core.Network;
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
    private readonly IConnectivityCheck _connectivity;
    private readonly ThresholdOptions _thresholds;
    private readonly ILogger<WingetService> _logger;

    /// <summary>Se intenta reparar las fuentes una sola vez por corrida.</summary>
    private bool _sourceRepairAttempted;

    /// <summary>
    /// El catálogo quedó confirmado como ilegible: la reparación se hizo y no sirvió.
    /// </summary>
    /// <remarks>
    /// Los paquetes de winget que falten se resuelven sin lanzar el proceso. En el equipo del
    /// 2026-08-29 los tres paquetes siguientes repitieron el mismo error uno por uno, cada uno con su
    /// propio párrafo idéntico en el log recomendando la reparación que ya se había hecho y ya había
    /// fallado. No aporta información y hace perder segundos por paquete.
    /// <para>Los de descarga directa <b>sí</b> se siguen intentando: no usan el catálogo.</para>
    /// </remarks>
    private WingetResult? _catalogUnusable;

    /// <summary>Tabla de códigos del winget del equipo. Se carga una vez por corrida.</summary>
    private WingetErrorTable? _errorTable;

    public WingetService(
        IProcessRunner runner,
        IWingetLocator locator,
        DirectDownloadInstaller directInstaller,
        IConnectivityCheck connectivity,
        ThresholdOptions thresholds,
        ILogger<WingetService> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(directInstaller);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _locator = locator;
        _directInstaller = directInstaller;
        _connectivity = connectivity;
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
        _catalogUnusable = null;
        _errorTable = null;

        // LA COMPUERTA DE RED. Va antes de todo y se evalúa una sola vez.
        //
        // Todo lo que instala EasyFix se descarga en el momento, así que sin internet no hay nada que
        // intentar. Sin esta comprobación, en el equipo del 2026-08-29 el proceso fue: la descarga
        // directa de Chrome falló con SocketException 11001 (DNS caído), y 365 ms después arrancó la
        // primera llamada a winget, que devolvió «catálogo no disponible» — un error real pero cuyo
        // motivo era la misma falta de red. EasyFix lo atribuyó tres veces a la sesión elevada, corrió
        // una reparación de fuentes que no podía servir de nada, y declaró «Fuentes de winget
        // reparadas» ocho segundos antes de que el mismo error volviera.
        //
        // La evidencia de que no había red ya estaba en el log. Lo que faltaba era consumirla.
        if (packages.Count > 0)
        {
            ConnectivityResult connectivity = await _connectivity.CheckAsync(ct).ConfigureAwait(false);

            if (!connectivity.IsOnline)
            {
                _logger.LogWarning(
                    "No se instala nada: sin internet ({Status}). {Technical}",
                    connectivity.Status, connectivity.TechnicalDetail);

                for (int i = 0; i < packages.Count; i++)
                {
                    WingetPackage offline = packages[i];
                    var result = WingetResultParser.Offline(offline.Id, connectivity);

                    progress?.Report(new InstallProgress(
                        offline.Id, offline.Name, i + 1, packages.Count, result));

                    results.Add(result);
                }

                return results;
            }
        }

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
            else
            {
                // La ruta se resuelve ANTES DE CADA PAQUETE, no una vez por tanda: winget se
                // autoactualiza en segundo plano y la carpeta de su paquete cambia de nombre. En la
                // primera prueba real pasó de _1.29.280.0 a _1.29.290.0 a mitad de la tanda, y las
                // instalaciones siguientes fallaron con "Acceso denegado" contra una ruta que ya no
                // existía.
                string? winget = _locator.Find();

                if (winget is null)
                {
                    result = WingetResultParser.Missing(package.Id);
                }
                else if (_catalogUnusable is { } known)
                {
                    // Ya se comprobó en esta corrida que el catálogo no se puede leer y que repararlo
                    // no sirvió. Lanzar winget de nuevo daría el mismo error, más lento.
                    _logger.LogInformation(
                        "{Package}: no se intenta, el catálogo de winget ya quedó descartado en esta " +
                        "corrida.", package.Id);

                    result = known with { PackageId = package.Id };
                }
                else
                {
                    await LoadErrorTableAsync(winget, ct).ConfigureAwait(false);

                    // Esperar a que Windows Installer esté libre. "En serie" no alcanza: el
                    // instalador anterior puede seguir finalizando. Es la explicación más probable
                    // del código 1 de VLC, que arrancó en el mismo segundo en que terminó VCRedist.
                    await WaitForInstallerAsync(progress, package, i, packages.Count, ct)
                        .ConfigureAwait(false);

                    result = await InstallOneAsync(winget, package, ct).ConfigureAwait(false);

                    // Reparar las fuentes y reintentar una vez. Es el fallo más común al correr
                    // elevado: el catálogo se instala por usuario y la sesión de administrador no lo ve.
                    if (result.Outcome == WingetOutcome.SourceUnavailable && !_sourceRepairAttempted)
                    {
                        _sourceRepairAttempted = true;

                        if (await TryRepairSourcesAsync(winget, ct).ConfigureAwait(false))
                        {
                            // NO se dice «reparadas» acá. Que «source reset» devuelva 0 significa que
                            // el comando corrió, no que el catálogo quedó legible: en el equipo del
                            // 2026-08-29 el log afirmó «Fuentes de winget reparadas» y 8,2 s después
                            // volvió el mismísimo error. La única prueba de que se reparó es que el
                            // reintento funcione, así que se espera a tenerla.
                            _logger.LogInformation(
                                "Se ejecutó la reparación de fuentes. Reintentando {Package} para ver " +
                                "si sirvió.", package.Id);

                            result = await InstallOneAsync(winget, package, ct).ConfigureAwait(false);

                            if (result.Outcome == WingetOutcome.SourceUnavailable)
                            {
                                _logger.LogError(
                                    "La reparación de fuentes NO sirvió: {Package} sigue devolviendo " +
                                    "el mismo error de catálogo. No se vuelve a intentar en esta corrida.",
                                    package.Id);

                                _catalogUnusable = result;
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "La reparación de fuentes sirvió: {Package} pasó a {Outcome}.",
                                    package.Id, result.Outcome);
                            }
                        }
                    }
                    else if (result.ExitCode is int code && WingetErrorCodes.IsWorthRetrying(code))
                    {
                        // Fallo transitorio: descarga dañada. Un reintento resuelve la mayoría.
                        _logger.LogInformation(
                            "{Package} falló con {Code}, que suele ser transitorio. Reintentando una vez.",
                            package.Id, $"0x{code:X8}");

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
    /// Espera a que Windows Installer esté libre.
    /// </summary>
    /// <remarks>
    /// Dos instaladores a la vez se pelean por el mutex global <c>_MSIExecute</c>: uno falla, o peor,
    /// queda a medias. Instalar en serie no alcanza, porque el instalador anterior puede seguir
    /// finalizando cuando arranca el siguiente — en la primera prueba real VLC arrancó en el mismo
    /// segundo en que terminó Visual C++ Redistributable y devolvió código 1.
    /// <para>Si el mutex no se libera en el tope, se sigue igual: bloquear la tanda entera por una
    /// espera sería peor que intentar y reportar el fallo.</para>
    /// </remarks>
    private async Task WaitForInstallerAsync(
        IProgress<InstallProgress>? progress,
        WingetPackage package,
        int index,
        int total,
        CancellationToken ct)
    {
        TimeSpan limit = TimeSpan.FromMinutes(3);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
        bool reported = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsWindowsInstallerBusy())
            {
                return;
            }

            if (!reported)
            {
                reported = true;
                _logger.LogInformation(
                    "Windows Installer está ocupado; esperando antes de instalar {Package}.", package.Id);
                progress?.Report(new InstallProgress(
                    package.Id, $"{package.Name} (esperando a que termine la instalación anterior)",
                    index + 1, total));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Windows Installer siguió ocupado tras {Minutes} min. Se instala {Package} igual.",
            limit.TotalMinutes, package.Id);
    }

    /// <summary>
    /// <c>true</c> si el mutex de Windows Installer está tomado.
    /// </summary>
    /// <remarks>
    /// Solo se consulta su existencia; no se toma ni se libera. Tomarlo sería peor: bloquearía a los
    /// instaladores legítimos.
    /// </remarks>
    private static bool IsWindowsInstallerBusy()
    {
        try
        {
            // El nombre lo define Windows Installer. Si existe, hay una instalación en curso.
            using var mutex = System.Threading.Mutex.OpenExisting(@"Global\_MSIExecute");
            return true;
        }
        catch (System.Threading.WaitHandleCannotBeOpenedException)
        {
            return false;   // no existe: nadie está instalando
        }
        catch (UnauthorizedAccessException)
        {
            // Existe pero sin permiso para abrirlo: igual significa que está tomado.
            return true;
        }
    }

    /// <summary>
    /// Carga la tabla de códigos del winget del equipo, una sola vez por corrida.
    /// </summary>
    /// <remarks>
    /// <c>winget error --output</c> exporta todos los códigos con su símbolo y descripción. Tenerla
    /// hace que el detalle de un fallo no dependa de constantes nuestras, que ya estuvieron mal una
    /// vez. Si el comando no existe en esa versión, se sigue con las constantes de respaldo.
    /// </remarks>
    private async Task LoadErrorTableAsync(string wingetPath, CancellationToken ct)
    {
        if (_errorTable is not null)
        {
            return;
        }

        _errorTable = new WingetErrorTable();   // vacía: evita reintentar en cada paquete

        string outputPath = Path.Combine(
            Path.GetTempPath(), $"easyfix-winget-errors-{Environment.ProcessId}.txt");

        try
        {
            ProcessResult result = await _runner
                .RunAsync(wingetPath, new[] { "error", "--output", outputPath },
                    TimeSpan.FromSeconds(60), ct)
                .ConfigureAwait(false);

            if (result.Succeeded && File.Exists(outputPath))
            {
                _errorTable = WingetErrorTable.Parse(await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false));
                _logger.LogInformation(
                    "Tabla de códigos de winget cargada: {Count} entradas.", _errorTable.Count);
            }
            else
            {
                // Normal en versiones de winget que no tienen el comando. Se usan las constantes.
                _logger.LogInformation(
                    "«winget error» no está disponible (código {Code}); se usan los códigos de respaldo.",
                    result.ExitCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "No se pudo cargar la tabla de códigos de winget.");
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath)) { File.Delete(outputPath); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
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

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                ProcessResult process = await _runner
                    .RunAsync(wingetPath, arguments, _thresholds.ExternalProcessTimeout, ct)
                    .ConfigureAwait(false);

                WingetResult result = WingetResultParser.Parse(package.Id, process, _errorTable);

                _logger.LogInformation(
                    "{PackageId}: {Outcome} (código {Code}). {Detail}",
                    package.Id, result.Outcome, result.ExitCode, result.Detail);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.ComponentModel.Win32Exception ex) when (
                attempt == 1 && (ex.NativeErrorCode == 5 || ex.NativeErrorCode == 2))
            {
                // Acceso denegado (5) o no encontrado (2): la carpeta del paquete de winget se
                // reemplazó por una autoactualización. Se vuelve a resolver la ruta y se reintenta.
                string? refreshed = _locator.Find();

                _logger.LogWarning(
                    "Error {Code} al lanzar winget en {Old}. winget pudo haberse actualizado; " +
                    "se reintenta con {New}.",
                    ex.NativeErrorCode, wingetPath, refreshed ?? "(no encontrado)");

                if (refreshed is null)
                {
                    return WingetResultParser.Missing(package.Id);
                }

                wingetPath = refreshed;
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

        return new WingetResult(
            package.Id, WingetOutcome.Failed,
            $"No se pudo lanzar winget para {package.Id} ni después de volver a resolver su ruta.",
            null);
    }
}
