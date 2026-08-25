using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using EasyFix.Core.Recommendations;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging;

namespace EasyFix.App.ViewModels;

/// <summary>Qué panel se está mostrando.</summary>
public enum Screen
{
    Home,
    Scanning,
    Report,
    Apps,
    Repairing,
    RepairResult,
}

/// <summary>Una línea del reporte, ya lista para mostrar.</summary>
/// <param name="Title">Título.</param>
/// <param name="Detail">Explicación.</param>
/// <param name="Metrics">Los números, en una línea.</param>
/// <param name="Kind">Color de la franja: <c>crit</c>, <c>warn</c>, <c>ok</c> o <c>info</c>.</param>
public sealed record ReportRow(string Title, string Detail, string? Metrics, string Kind);

/// <summary>
/// Pantalla principal. Sin lógica de negocio: orquesta la sonda, las reglas, el motor de
/// recomendaciones, el instalador y el ejecutor de reparaciones — todos de <c>EasyFix.Core</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly EasyFixOptions _options;
    private readonly SystemProbe _probe;
    private readonly HardwareAdvisor _advisor;
    private readonly WingetService _winget;
    private readonly FixRunner _fixRunner;
    private readonly IEnumerable<IFix> _fixes;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _cts;

    /// <summary>Último análisis. Las reparaciones necesitan el snapshot y el análisis de pantallazos.</summary>
    private ProbeResult? _lastProbe;
    private CrashAnalysis? _lastCrash;

    public MainViewModel(
        EasyFixOptions options,
        SystemProbe probe,
        HardwareAdvisor advisor,
        WingetService winget,
        FixRunner fixRunner,
        IEnumerable<IFix> fixes,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(winget);
        ArgumentNullException.ThrowIfNull(fixRunner);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _probe = probe;
        _advisor = advisor;
        _winget = winget;
        _fixRunner = fixRunner;
        _fixes = fixes;
        _logger = logger;

        foreach (WingetPackage package in options.WingetPackages)
        {
            Apps.Add(new AppChoice(package));
        }

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        RepairCommand = new AsyncRelayCommand(RepairAsync, () => !IsBusy);
        RunInstallCommand = new AsyncRelayCommand(InstallSelectedAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        BackCommand = new RelayCommand(() => CurrentScreen = Screen.Home);

        InstallAppsCommand = new RelayCommand(() =>
        {
            StatusMessage = null;
            CurrentScreen = Screen.Apps;
        });

        DiagnoseWingetCommand = new AsyncRelayCommand(DiagnoseWingetAsync, () => !IsBusy);
    }

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand RepairCommand { get; }
    public IAsyncRelayCommand RunInstallCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand InstallAppsCommand { get; }
    public IAsyncRelayCommand DiagnoseWingetCommand { get; }

    // ---- Estado de la interfaz --------------------------------------------------------------

    [ObservableProperty]
    private Screen _currentScreen = Screen.Home;

    [ObservableProperty]
    private string _systemSummary = "Pulsá «Analizar el equipo» para empezar.";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _statusIsWarning;

    [ObservableProperty]
    private string _progressLabel = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RepairCommand.NotifyCanExecuteChanged();
        RunInstallCommand.NotifyCanExecuteChanged();
        DiagnoseWingetCommand.NotifyCanExecuteChanged();
    }

    // ---- Marca ------------------------------------------------------------------------------

    /// <summary>Nombre del producto, de la configuración.</summary>
    public string ProductName => _options.Branding.ProductName;

    /// <summary>"por Andrés Hernández", o vacío si no hay nombre configurado.</summary>
    public string? Attribution => _options.Branding.Attribution;

    /// <summary>Lo que se puede arreglar sin comprar nada.</summary>
    public ObservableCollection<ReportRow> SoftwareRows { get; } = new();

    /// <summary>Lo que necesita comprar algo o abrir el equipo.</summary>
    public ObservableCollection<ReportRow> HardwareRows { get; } = new();

    /// <summary>Lo que no se pudo medir. Se muestra: un chequeo que falló no es "todo bien".</summary>
    public ObservableCollection<ReportRow> UndeterminedRows { get; } = new();

    /// <summary>Resultado de la última reparación.</summary>
    public ObservableCollection<ReportRow> RepairRows { get; } = new();

    /// <summary>Los programas configurados, con su casilla.</summary>
    public ObservableCollection<AppChoice> Apps { get; } = new();

    public bool HasSoftwareRows => SoftwareRows.Count > 0;
    public bool HasHardwareRows => HardwareRows.Count > 0;
    public bool HasUndeterminedRows => UndeterminedRows.Count > 0;

    /// <summary><c>true</c> cuando el disco está fallando: se bloquea todo lo demás.</summary>
    [ObservableProperty]
    private bool _fixesBlocked;

    /// <summary><c>true</c> si el equipo tiene BitLocker: se pide confirmar la clave.</summary>
    [ObservableProperty]
    private bool _bitLockerActive;

    /// <summary>
    /// El técnico confirmó tener la clave de recuperación de BitLocker a mano.
    /// </summary>
    /// <remarks>
    /// Sin esto, las reparaciones que tocan arranque o disco quedan bloqueadas. Es la única
    /// consecuencia verdaderamente irreversible que puede causar la herramienta: si Windows pide la
    /// clave al reiniciar y el cliente no la tiene, queda fuera de su propio equipo.
    /// </remarks>
    [ObservableProperty]
    private bool _bitLockerKeyConfirmed;

    [ObservableProperty]
    private bool _rebootRequired;

    /// <summary>
    /// El técnico eligió aplicar cambios aunque no se pueda crear el punto de restauración.
    /// </summary>
    /// <remarks>
    /// Nunca es el default. En muchos equipos Restaurar sistema viene deshabilitado de fábrica, y
    /// bloquear todo por eso dejaba la herramienta inservible — que es lo que pasó en la primera
    /// prueba real. El journal sigue registrando cada cambio, así que «Deshacer todo» funciona
    /// igual; lo que se pierde es el respaldo del sistema completo.
    /// </remarks>
    [ObservableProperty]
    private bool _allowWithoutRestorePoint;

    [ObservableProperty]
    private string? _installProgressLabel;

    public int ConfiguredPackageCount => _options.WingetPackages.Count;

    // ---- Analizar ---------------------------------------------------------------------------

    private async Task AnalyzeAsync()
    {
        SoftwareRows.Clear();
        HardwareRows.Clear();
        UndeterminedRows.Clear();
        StatusMessage = null;
        ProgressLabel = "Iniciando…";
        CurrentScreen = Screen.Scanning;

        ResetCancellation();
        var progress = new Progress<string>(label => ProgressLabel = label);

        try
        {
            ProbeResult result = await _probe.ProbeAsync(progress, _cts!.Token);
            _lastProbe = result;
            _lastCrash = CrashAnalyzer.Analyze(result.Crash);

            BuildReport(result, _lastCrash);
            CurrentScreen = Screen.Report;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("El análisis se canceló.");
            CurrentScreen = Screen.Home;
            SetStatus("Análisis cancelado. No se modificó nada.", warning: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "El análisis falló.");
            CurrentScreen = Screen.Home;
            SetStatus($"El análisis falló: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
    }

    private void BuildReport(ProbeResult result, CrashAnalysis crash)
    {
        SystemSnapshot snapshot = result.Snapshot;

        SystemSummary = result.Identity.Describe(snapshot);
        FixesBlocked = _advisor.ShouldBlockFixes(snapshot);
        BitLockerActive = snapshot.BitLockerActive;

        // Los pantallazos van primero: es lo más grave que puede tener un equipo.
        foreach (Finding finding in CrashAnalyzer.ToFindings(crash))
        {
            // Lo confirmado como hardware no se arregla con software: va a la otra columna.
            bool isHardware = finding.CheckId is "crash.whea" ||
                              (finding.CheckId == "crash.bluescreen" && crash.HardwareConfirmed);

            (isHardware ? HardwareRows : SoftwareRows).Add(ToRow(finding));
        }

        foreach (Finding finding in SoftwareFindings.Evaluate(snapshot, _options.Thresholds))
        {
            SoftwareRows.Add(ToRow(finding));
        }

        foreach (Recommendation rec in _advisor.Advise(snapshot))
        {
            var row = new ReportRow(
                rec.Title,
                rec.Detail,
                rec.Evidence?.ToString(),
                rec.Priority switch
                {
                    RecommendationPriority.Urgent => "crit",
                    RecommendationPriority.TopImpact => "warn",
                    _ => "info",
                });

            (rec.NeedsHardware ? HardwareRows : SoftwareRows).Add(row);
        }

        foreach (CheckFailure failure in result.Failures)
        {
            UndeterminedRows.Add(new ReportRow(
                DescribeProbe(failure.CheckId), failure.Reason, null, "info"));
        }

        NotifyReportChanged();

        if (SoftwareRows.Count == 0 && HardwareRows.Count == 0)
        {
            SetStatus(
                "No se encontró nada para mejorar. El equipo está bien: cualquier limpieza acá daría " +
                "entre 0 y 5 % y no vale la pena tocarlo.",
                warning: false);
        }
        else if (result.Failures.Count > 0)
        {
            SetStatus(
                $"El reporte está incompleto: {result.Failures.Count} comprobación(es) no se pudo " +
                "determinar. Están listadas abajo.",
                warning: true);
        }
    }

    // ---- Reparar ----------------------------------------------------------------------------

    /// <summary>
    /// Aplica las reparaciones. <see cref="FixRunner"/> tiene todas las compuertas de seguridad:
    /// disco fallando aborta, sin punto de restauración aborta, BitLocker sin confirmar bloquea lo
    /// que toca arranque o disco.
    /// </summary>
    private async Task RepairAsync()
    {
        if (_lastProbe is null)
        {
            SetStatus("Primero hay que analizar el equipo.", warning: true);
            return;
        }

        RepairRows.Clear();
        StatusMessage = null;
        ProgressLabel = "Preparando…";
        IsBusy = true;
        CurrentScreen = Screen.Repairing;

        ResetCancellation();
        var progress = new Progress<string>(label => ProgressLabel = label);

        FileJournalSink? sink = null;
        try
        {
            string runId = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH-mm-ss");
            sink = new FileJournalSink(FileJournalSink.PathForRun(runId));

            RunJournal journal = RunJournal.Start(
                new JournalHeader
                {
                    RunId = runId,
                    StartedUtc = DateTimeOffset.UtcNow,
                    MachineName = Environment.MachineName,
                    OsVersion = _lastProbe.Identity.OsCaption,
                    DomainJoined = _lastProbe.Snapshot.IsDomainJoined,
                    BitLockerWasSuspended = _lastProbe.Snapshot.BitLockerActive && BitLockerKeyConfirmed,
                    BaselineMainPathBootTimeMs = _lastProbe.Snapshot.MainPathBootTimeMs,
                },
                sink);

            RunResult result = await _fixRunner.RunAsync(
                _fixes.ToList(),
                _lastProbe.Snapshot,
                _lastCrash,
                journal,
                BitLockerKeyConfirmed,
                AllowWithoutRestorePoint,
                progress,
                _cts!.Token);

            ShowRepairResult(result, sink.FilePath);
            CurrentScreen = Screen.RepairResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("La reparación se canceló.");
            CurrentScreen = Screen.Report;
            SetStatus(
                "Reparación cancelada. Lo que ya se había aplicado quedó registrado y se puede deshacer.",
                warning: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "La reparación falló.");
            CurrentScreen = Screen.Report;
            SetStatus($"La reparación falló: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            sink?.Dispose();
            IsBusy = false;
            ProgressLabel = string.Empty;
        }
    }

    private void ShowRepairResult(RunResult result, string journalPath)
    {
        RebootRequired = result.RebootRequired;

        if (result.Aborted)
        {
            RepairRows.Add(new ReportRow(
                "No se aplicó ningún cambio", result.AbortReason ?? string.Empty, null, "crit"));
            SetStatus("La reparación se abortó antes de tocar nada.", warning: true);
            return;
        }

        foreach (FixReport report in result.Results)
        {
            if (report.Blocked is { } blocked)
            {
                RepairRows.Add(new ReportRow(
                    report.DisplayName,
                    blocked.Explanation,
                    "No se aplicó",
                    blocked.Reason == FixBlockReason.NotNeeded ? "info" : "warn"));
                continue;
            }

            FixOutcome outcome = report.Outcome!;

            RepairRows.Add(new ReportRow(
                report.DisplayName,
                outcome.Summary,
                outcome.Status switch
                {
                    FixStatus.Applied => "Hecho",
                    FixStatus.AppliedNeedsReboot => "Hecho — falta reiniciar",
                    FixStatus.NothingToDo => "No hacía falta",
                    FixStatus.Failed => "Falló",
                    _ => null,
                },
                outcome.Status switch
                {
                    FixStatus.Failed => "crit",
                    FixStatus.AppliedNeedsReboot => "warn",
                    FixStatus.NothingToDo => "info",
                    _ => "ok",
                }));
        }

        int applied = result.Applied.Count();
        int failed = result.Failed.Count();

        var parts = new List<string>
        {
            result.RanWithoutRestorePoint
                ? "SIN punto de restauración (por tu decisión)"
                : $"Punto de restauración {result.RestorePointSequence} creado",
        };
        if (applied > 0) { parts.Add($"{applied} reparación(es) aplicada(s)"); }
        if (failed > 0) { parts.Add($"{failed} con error"); }
        if (result.RebootRequired) { parts.Add("hace falta reiniciar para completar"); }

        SetStatus(
            string.Join(" · ", parts) + $". Registro en {journalPath}",
            warning: failed > 0 || result.RebootRequired || result.RanWithoutRestorePoint);
    }

    // ---- Instalar programas -----------------------------------------------------------------

    private async Task InstallSelectedAsync()
    {
        List<AppChoice> selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No hay ningún programa marcado.", warning: false);
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        foreach (AppChoice app in selected) { app.Status = null; }

        var byId = selected.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgressLabel = p.Finished is null
                ? $"({p.Index}/{p.Total}) Descargando e instalando {p.DisplayName}…"
                : $"({p.Index}/{p.Total}) {p.DisplayName}";

            if (!byId.TryGetValue(p.PackageId, out AppChoice? app)) { return; }

            if (p.Finished is null) { app.MarkInstalling(); }
            else { app.MarkResult(p.Finished); }
        });

        try
        {
            IReadOnlyList<WingetResult> results = await _winget.InstallAsync(
                selected.Select(a => a.Package).ToList(), progress, CancellationToken.None);

            InstallProgressLabel = null;
            SetStatus(
                WingetResultParser.Summarize(results),
                warning: results.Any(r => !r.PackageAvailable));

            foreach (WingetResult failed in results.Where(r => !r.PackageAvailable))
            {
                _logger.LogWarning("{PackageId}: {Detail}", failed.PackageId, failed.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "La instalación falló.");
            InstallProgressLabel = null;
            SetStatus($"La instalación falló: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Muestra dónde está winget, su versión y si puede leer su catálogo. Solo lectura.
    /// </summary>
    /// <remarks>
    /// Existe por el fallo más difícil de diagnosticar del módulo: winget corriendo elevado no ve su
    /// propio catálogo, porque se instala por usuario. Sin este botón, el técnico solo veía «Falló».
    /// </remarks>
    private async Task DiagnoseWingetAsync()
    {
        IsBusy = true;
        try
        {
            string report = await _winget.DiagnoseAsync(CancellationToken.None);
            _logger.LogInformation("Diagnóstico de winget:\n{Report}", report);
            SetStatus(report, warning: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "El diagnóstico de winget falló.");
            SetStatus($"El diagnóstico de winget falló: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private void ResetCancellation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
    }

    private static ReportRow ToRow(Finding finding) => new(
        finding.Title,
        finding.Detail,
        finding.AllMetrics.Count == 0 ? null : string.Join("  ·  ", finding.AllMetrics.Select(m => m.ToString())),
        finding.Severity switch
        {
            Severity.Critical => "crit",
            Severity.Warning => "warn",
            _ => "info",
        });

    private void NotifyReportChanged()
    {
        OnPropertyChanged(nameof(HasSoftwareRows));
        OnPropertyChanged(nameof(HasHardwareRows));
        OnPropertyChanged(nameof(HasUndeterminedRows));
    }

    /// <summary>Traduce el id técnico de una sonda a algo que se pueda leer en el reporte.</summary>
    private static string DescribeProbe(string probeId) => probeId switch
    {
        "os" => "Sistema operativo y equipo",
        "cpu" => "Procesador",
        "memory" => "Memoria instalada",
        "memory.pressure" => "Presión de memoria",
        "disk.logical" => "Espacio en disco",
        "disk.physical" => "Tipo y salud del disco",
        "disk.smart" => "Estado SMART del disco",
        "disk.latency" => "Latencia del disco",
        "startup" => "Programas que arrancan con Windows",
        "boot" => "Tiempo del último arranque",
        "antivirus" => "Antivirus activos",
        "bitlocker" => "Estado de BitLocker",
        "printers" => "Impresoras instaladas",
        "bluetooth" => "Adaptador Bluetooth",
        "crash" => "Pantallazos azules y estabilidad",
        _ => probeId,
    };

    private void SetStatus(string message, bool warning)
    {
        StatusIsWarning = warning;
        StatusMessage = message;
    }
}
