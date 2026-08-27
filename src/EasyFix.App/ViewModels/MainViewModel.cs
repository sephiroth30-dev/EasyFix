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
    Undo,
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
    private readonly UndoEngine _undoEngine;
    private readonly JournalStore _journals;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>Corrida que se va a deshacer. Se resuelve al abrir la pantalla.</summary>
    private StoredRun? _undoTarget;

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
        UndoEngine undoEngine,
        JournalStore journals,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(winget);
        ArgumentNullException.ThrowIfNull(fixRunner);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(undoEngine);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _probe = probe;
        _advisor = advisor;
        _winget = winget;
        _fixRunner = fixRunner;
        _fixes = fixes;
        _undoEngine = undoEngine;
        _journals = journals;
        _logger = logger;

        foreach (WingetPackage package in options.WingetPackages)
        {
            Apps.Add(new AppChoice(package));
        }

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        ImproveCommand = new AsyncRelayCommand(
            () => RunFixesAsync(FixCategory.Performance), () => !IsBusy);
        RepairCommand = new AsyncRelayCommand(
            () => RunFixesAsync(FixCategory.Repair), () => !IsBusy);
        RunInstallCommand = new AsyncRelayCommand(InstallSelectedAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        BackCommand = new RelayCommand(() => CurrentScreen = Screen.Home);

        InstallAppsCommand = new RelayCommand(() =>
        {
            StatusMessage = null;
            CurrentScreen = Screen.Apps;
        });

        DiagnoseWingetCommand = new AsyncRelayCommand(DiagnoseWingetAsync, () => !IsBusy);

        OpenUndoCommand = new RelayCommand(OpenUndo);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !IsBusy && _undoTarget is not null);

        // Al arrancar se busca si quedó algo para deshacer de una visita anterior.
        RefreshUndoAvailability();
    }

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IAsyncRelayCommand ImproveCommand { get; }
    public IAsyncRelayCommand RepairCommand { get; }
    public IAsyncRelayCommand RunInstallCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand InstallAppsCommand { get; }
    public IAsyncRelayCommand DiagnoseWingetCommand { get; }
    public IRelayCommand OpenUndoCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }

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
        ImproveCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        RunInstallCommand.NotifyCanExecuteChanged();
        DiagnoseWingetCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---- Deshacer ---------------------------------------------------------------------------

    /// <summary>Lo que se va a revertir, una fila por acción.</summary>
    public ObservableCollection<ReportRow> UndoRows { get; } = new();

    /// <summary><c>true</c> si hay una corrida anterior con algo que revertir.</summary>
    [ObservableProperty]
    private bool _canUndo;

    /// <summary>Resumen de la corrida a deshacer, para la pantalla de inicio.</summary>
    [ObservableProperty]
    private string? _undoSummary;

    /// <summary>
    /// Busca si hay algo para deshacer.
    /// </summary>
    /// <remarks>
    /// Se consulta al arrancar y después de cada reparación. Los journals viven en
    /// <c>%ProgramData%</c>, así que esto funciona entre sesiones: el técnico repara hoy, cierra la
    /// app, y la semana que viene vuelve al mismo equipo y puede revertir.
    /// </remarks>
    private void RefreshUndoAvailability()
    {
        try
        {
            _undoTarget = _journals.FindLatestUndoable();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo buscar corridas para deshacer.");
            _undoTarget = null;
        }

        CanUndo = _undoTarget is not null;

        UndoSummary = _undoTarget is { } run
            ? $"Última reparación: {run.ModifiedUtc.ToLocalTime():dd/MM HH:mm} · " +
              $"{run.ReversibleCount} cambio(s) reversible(s)"
            : null;

        UndoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Muestra qué se va a revertir ANTES de hacerlo.
    /// </summary>
    /// <remarks>
    /// Deshacer también es un cambio en el equipo, así que no se aplica de un click: primero se lista
    /// qué se va a tocar y qué no se puede recuperar.
    /// </remarks>
    private void OpenUndo()
    {
        RefreshUndoAvailability();
        UndoRows.Clear();
        StatusMessage = null;

        if (_undoTarget is not { } run)
        {
            SetStatus("No hay ninguna reparación anterior con cambios reversibles.", warning: false);
            return;
        }

        foreach (JournalAction action in run.Journal.Actions.Reverse())
        {
            bool reversible = action.Reversible && action.Undo is not null;

            UndoRows.Add(new ReportRow(
                DescribeFix(action.FixId),
                action.Note ?? action.Target,
                reversible ? "Se revierte" : "NO se puede revertir",
                reversible ? "ok" : "warn"));
        }

        long lost = run.Journal.Actions.Sum(a => a.Reversible ? 0 : a.FreedBytes ?? 0);

        var parts = new List<string>
        {
            $"{run.ReversibleCount} cambio(s) se revierten",
        };

        if (run.IrreversibleCount > 0)
        {
            parts.Add($"{run.IrreversibleCount} no se pueden revertir");
        }

        if (lost > 0)
        {
            parts.Add($"{lost / 1024.0 / 1024.0:0.#} MB de archivos borrados no vuelven");
        }

        if (run.Journal.Header?.RestorePointSequence is long sequence)
        {
            parts.Add($"hay punto de restauración {sequence} como respaldo");
        }
        else
        {
            parts.Add("esa corrida NO tuvo punto de restauración");
        }

        SetStatus(string.Join(" · ", parts) + ".", warning: run.IrreversibleCount > 0);
        CurrentScreen = Screen.Undo;
    }

    private async Task UndoAsync()
    {
        if (_undoTarget is not { } run)
        {
            return;
        }

        IsBusy = true;
        ProgressLabel = "Revirtiendo cambios…";

        try
        {
            UndoReport report = await _undoEngine.UndoAsync(run.Journal, CancellationToken.None);

            _journals.MarkUndone(run);

            UndoRows.Clear();

            foreach (JournalAction action in report.Undone)
            {
                UndoRows.Add(new ReportRow(
                    DescribeFix(action.FixId), action.Note ?? action.Target, "Revertido", "ok"));
            }

            foreach ((JournalAction action, string error) in report.Failed)
            {
                UndoRows.Add(new ReportRow(
                    DescribeFix(action.FixId), error, "Falló", "crit"));
            }

            foreach (JournalAction action in report.NoHandler)
            {
                UndoRows.Add(new ReportRow(
                    DescribeFix(action.FixId),
                    "No hay forma automática de revertir este cambio. Queda el punto de restauración.",
                    "Sin revertir", "warn"));
            }

            foreach (JournalAction action in report.NotReversible)
            {
                UndoRows.Add(new ReportRow(
                    DescribeFix(action.FixId),
                    action.Note ?? action.Target, "No era reversible", "info"));
            }

            var parts = new List<string> { $"{report.Undone.Count} cambio(s) revertido(s)" };
            if (report.Failed.Count > 0) { parts.Add($"{report.Failed.Count} con error"); }
            if (report.NoHandler.Count > 0) { parts.Add($"{report.NoHandler.Count} sin forma de revertir"); }
            if (report.BytesNotRecoverable > 0)
            {
                parts.Add($"{report.BytesNotRecoverable / 1024.0 / 1024.0:0.#} MB borrados no vuelven");
            }

            SetStatus(string.Join(" · ", parts) + ".", warning: !report.FullySucceeded);

            _logger.LogInformation(
                "Deshacer {RunId}: {Undone} revertidos, {Failed} con error, {NoHandler} sin handler.",
                run.RunId, report.Undone.Count, report.Failed.Count, report.NoHandler.Count);

            RefreshUndoAvailability();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deshacer falló.");
            SetStatus($"No se pudo deshacer: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
        finally
        {
            IsBusy = false;
            ProgressLabel = string.Empty;
        }
    }

    /// <summary>Traduce el id de un fix a algo legible en la pantalla de deshacer.</summary>
    private static string DescribeFix(string fixId) => fixId switch
    {
        "system.repair-files" => "Reparación de archivos del sistema",
        "disk.chkdsk" => "Revisión del disco",
        "update.reset-components" => "Restablecimiento de Windows Update",
        "network.reset" => "Restablecimiento de la red",
        "memory.schedule-test" => "Diagnóstico de memoria programado",
        "update.remove" => "Actualización desinstalada",
        "restorepoint.throttle" => "Límite de puntos de restauración",
        "restorepoint.skipped" => "Se trabajó sin punto de restauración",
        "bitlocker.suspend" => "Suspensión de BitLocker",
        "startup.disable" => "Programa quitado del inicio",
        "temp.clean" => "Limpieza de temporales",
        _ => fixId,
    };

    // ---- Marca ------------------------------------------------------------------------------

    /// <summary>Nombre del producto, de la configuración.</summary>
    public string ProductName => _options.Branding.ProductName;

    /// <summary>"por Andrés Hernández", o vacío si no hay nombre configurado.</summary>
    public string? Attribution => _options.Branding.Attribution;

    /// <summary>
    /// Versión del ejecutable, visible en la ventana.
    /// </summary>
    /// <remarks>
    /// Está a la vista a propósito: cuando se prueban varios builds seguidos, un reporte de error sin
    /// versión no se puede atar a nada. Sale del ensamblado, no de la configuración, así que no se
    /// puede desincronizar del binario.
    /// </remarks>
    public string Version =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "?";

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

    /// <summary>Título de la pantalla de progreso: cambia según qué se esté aplicando.</summary>
    [ObservableProperty]
    private string _runningCategoryLabel = "Trabajando…";

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

            // Todo lo medido al log. Sin esto el log solo hablaba de fallos, así que no servía para
            // confirmar que el diagnóstico funcionó ni para revisar un dato que salió mal.
            _logger.LogInformation(
                "Diagnóstico completo. Equipo: {Machine}{NewLine}{Snapshot}",
                result.Identity.Describe(result.Snapshot).Replace("\n", " · ", StringComparison.Ordinal),
                Environment.NewLine,
                result.Snapshot.ToLogSummary());

            if (result.Failures.Count > 0)
            {
                _logger.LogWarning(
                    "Sondas sin determinar ({Count}):{NewLine}{Failures}",
                    result.Failures.Count,
                    Environment.NewLine,
                    string.Join(Environment.NewLine,
                        result.Failures.Select(f => $"  {f.CheckId}: {f.Reason}")));
            }

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

        // Los hallazgos también al log: un reporte se puede reconstruir desde el archivo sin
        // necesitar capturas de pantalla.
        _logger.LogInformation(
            "Hallazgos: {Software} de software, {Hardware} de hardware.{NewLine}{Rows}",
            SoftwareRows.Count, HardwareRows.Count, Environment.NewLine,
            string.Join(Environment.NewLine,
                SoftwareRows.Concat(HardwareRows)
                    .Select(r => $"  [{r.Kind}] {r.Title}" +
                                 (r.Metrics is null ? string.Empty : $" — {r.Metrics}"))));

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
    /// <summary>
    /// Aplica los fixes de una categoría. «Mejorar rendimiento» y «Reparar errores» usan el mismo
    /// motor y las mismas compuertas de seguridad; solo cambia qué fixes se le pasan.
    /// </summary>
    private async Task RunFixesAsync(FixCategory category)
    {
        if (_lastProbe is null)
        {
            SetStatus("Primero hay que analizar el equipo.", warning: true);
            return;
        }

        List<IFix> selected = _fixes.Where(f => f.Category == category).ToList();

        if (selected.Count == 0)
        {
            SetStatus("No hay ninguna acción implementada en esta categoría todavía.", warning: true);
            return;
        }

        RunningCategoryLabel = category == FixCategory.Performance
            ? "Mejorando el rendimiento…"
            : "Reparando el equipo…";

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
                selected,
                _lastProbe.Snapshot,
                _lastCrash,
                journal,
                BitLockerKeyConfirmed,
                AllowWithoutRestorePoint,
                progress,
                _cts!.Token);

            ShowRepairResult(result, sink.FilePath);
            CurrentScreen = Screen.RepairResult;

            // Cerrar el sink antes de buscar, para que el journal esté completo en disco.
            sink.Dispose();
            sink = null;
            RefreshUndoAvailability();
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
