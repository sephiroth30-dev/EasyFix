using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyFix.Core.Apps;
using EasyFix.Core.Classification;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using EasyFix.Core.Network;
using EasyFix.Core.Recommendations;
using EasyFix.Core.Reputation;
using EasyFix.Core.Rollback;
using EasyFix.Core.Verification;
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
    private readonly SaturationAdvisor _saturation;
    private readonly SoftwareSelectionAdvisor _softwareSelection;
    private readonly IInstalledProgramsReader _programs;
    private readonly StartupEntryReader _startupReader;
    private readonly StartupClassifier _startupClassifier;
    private readonly WingetService _winget;
    private readonly IConnectivityCheck _connectivity;
    private readonly FixRunner _fixRunner;
    private readonly IEnumerable<IFix> _fixes;
    private readonly UndoEngine _undoEngine;
    private readonly JournalStore _journals;
    private readonly ILogger<MainViewModel> _logger;

    /// <summary>El checklist del análisis, para la columna «antes» del después de reparar.</summary>
    private ChecklistReport? _checklistBefore;

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
        IInstalledProgramsReader programs,
        StartupEntryReader startupReader,
        StartupClassifier startupClassifier,
        WingetService winget,
        IConnectivityCheck connectivity,
        FixRunner fixRunner,
        IEnumerable<IFix> fixes,
        UndoEngine undoEngine,
        JournalStore journals,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(startupReader);
        ArgumentNullException.ThrowIfNull(startupClassifier);
        ArgumentNullException.ThrowIfNull(winget);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(fixRunner);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(undoEngine);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _probe = probe;
        _advisor = advisor;
        _saturation = new SaturationAdvisor(options.Thresholds);
        _softwareSelection = new SoftwareSelectionAdvisor(options.Thresholds);
        _programs = programs;
        _startupReader = startupReader;
        _startupClassifier = startupClassifier;
        _winget = winget;
        _connectivity = connectivity;
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
        OpenDefaultAppsCommand = new RelayCommand(OpenDefaultApps);

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
    public IRelayCommand OpenDefaultAppsCommand { get; }
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

    /// <summary>Qué se está haciendo ahora, en una frase corta.</summary>
    [ObservableProperty]
    private string _progressLabel = string.Empty;

    /// <summary>Sublínea con el detalle del paso actual.</summary>
    [ObservableProperty]
    private string? _progressDetail;

    /// <summary>Avance de 0 a 100 para la barra.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>"Paso 2 de 5" — le da al usuario una noción de cuánto falta.</summary>
    [ObservableProperty]
    private string? _progressStep;

    /// <summary>
    /// <c>true</c> mientras no se pueda calcular el avance real.
    /// </summary>
    /// <remarks>
    /// Se usa solo al arrancar cada operación, antes del primer reporte. Una barra indeterminada
    /// permanente no le dice nada al usuario sobre cuánto falta.
    /// </remarks>
    [ObservableProperty]
    private bool _progressIsIndeterminate = true;

    /// <summary>Pone la barra en cero e indeterminada al empezar una operación.</summary>
    private void ResetProgress(string label)
    {
        ProgressLabel = label;
        ProgressDetail = null;
        ProgressStep = null;
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
    }

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
        ResetProgress("Revirtiendo los cambios");

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

    /// <summary>Resultado de un paquete en una frase corta, para la línea de progreso.</summary>
    private static string DescribeOutcome(WingetResult result) => result.Outcome switch
    {
        WingetOutcome.Installed => "instalado",
        WingetOutcome.AlreadyInstalled => "ya estaba",
        WingetOutcome.NotInCatalog => "no está en el catálogo",
        WingetOutcome.SourceUnavailable => "sin catálogo de winget",
        WingetOutcome.NoNetwork => "sin internet",
        WingetOutcome.WingetMissing => "falta winget",
        WingetOutcome.TimedOut => "se pasó del tiempo",
        WingetOutcome.NoApplicableInstaller => "sin instalador compatible",
        _ => "falló",
    };

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

    /// <summary>
    /// El estado del equipo como lista de condiciones. En el reporte muestra la foto de HOY; en el
    /// resultado de la reparación, el antes → después por condición. Es el entregable que convierte
    /// «te lo dejé sin errores» en algo verificable.
    /// </summary>
    public ObservableCollection<ReportRow> ChecklistRows { get; } = new();

    /// <summary>Las mismas condiciones re-evaluadas tras reparar, como «antes → después».</summary>
    public ObservableCollection<ReportRow> ChecklistCompareRows { get; } = new();

    public bool HasChecklistRows => ChecklistRows.Count > 0;
    public bool HasChecklistCompareRows => ChecklistCompareRows.Count > 0;
    public bool HasRepairRows => RepairRows.Count > 0;

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
        ChecklistRows.Clear();
        ChecklistCompareRows.Clear();
        StatusMessage = null;
        ResetProgress("Analizando el equipo");
        CurrentScreen = Screen.Scanning;

        ResetCancellation();

        var progress = new Progress<ProbeProgress>(p =>
        {
            ProgressIsIndeterminate = false;
            ProgressPercent = p.Percent;
            ProgressDetail = p.Current;
            ProgressStep = $"{p.Completed} de {p.Total} comprobaciones";
        });

        try
        {
            // La conexión se comprueba EN PARALELO con las sondas, no después: tarda hasta 8 s y
            // sumarlos al final alargaría el análisis por algo que no depende de él.
            Task<ConnectivityResult> networkTask = _connectivity.CheckAsync(_cts!.Token);

            // Los programas instalados también en paralelo: son claves de registro, solo lectura.
            // El try vive DENTRO de la tarea: si el análisis se cancela antes de este await, una
            // excepción del lector no queda huérfana como UnobservedTaskException.
            Task<IReadOnlyList<InstalledProgram>?> programsTask = Task.Run(
                IReadOnlyList<InstalledProgram>? () =>
                {
                    try
                    {
                        return _programs.Read();
                    }
                    catch (Exception ex)
                    {
                        // Sin la lista no hay cruce de reputación; se dice en «no se pudo
                        // determinar», nunca se asume «cero programas malos».
                        _logger.LogWarning(ex, "No se pudieron leer los programas instalados.");
                        return null;
                    }
                },
                _cts!.Token);

            // Leer las entradas de inicio también en paralelo: implica verificar firmas
            // (X509Chain.Build, lo más lento de la app), así que va a un hilo del pool igual que el
            // fix real. La clasificación en sí se hace DESPUÉS, una vez que hay snapshot —necesita
            // el SystemContext real (dominio, SSD, RAM)—, pero es instantánea: la parte lenta es
            // esta lectura, no la clasificación.
            Task<IReadOnlyList<StartupEntry>?> startupEntriesTask = Task.Run(
                IReadOnlyList<StartupEntry>? () =>
                {
                    try
                    {
                        return _startupReader.Read();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "No se pudo leer los programas de inicio para la vista previa.");
                        return null;
                    }
                },
                _cts!.Token);

            ProbeResult result = await _probe.ProbeAsync(progress, _cts!.Token);

            ConnectivityResult network = await networkTask;

            IReadOnlyList<InstalledProgram>? installedPrograms = await programsTask;

            StartupPreview? startupPreview = null;
            if (await startupEntriesTask is { } startupEntries)
            {
                startupPreview = StartupPreview.Build(
                    startupEntries,
                    _startupClassifier,
                    new SystemContext(result.Snapshot.IsDomainJoined, result.Snapshot.IsSsd,
                        result.Snapshot.TotalRamGb ?? 0));
            }

            // Que el reporte lo diga ANTES de que el técnico apriete «Instalar programas». En el
            // equipo del 2026-08-29 se enteró al revés: cuatro fallos, y con el motivo equivocado.
            result = result with { Snapshot = result.Snapshot with { Network = network.Status } };

            if (!network.IsOnline)
            {
                _logger.LogWarning(
                    "Sin internet ({Status}): {Detail} {Technical}",
                    network.Status, network.Detail, network.TechnicalDetail);
            }

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

            // La foto «antes»: el mismo checklist se re-evalúa después de reparar y se muestran
            // las dos columnas. Es lo que convierte la mejora en algo verificable.
            _checklistBefore = ChecklistEvaluator.Evaluate(
                result.Snapshot, result.Crash, result.Failures, _options.Thresholds);

            BuildReport(result, _lastCrash, installedPrograms, startupPreview);
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

    private void BuildReport(
        ProbeResult result, CrashAnalysis crash, IReadOnlyList<InstalledProgram>? installedPrograms,
        StartupPreview? startupPreview)
    {
        SystemSnapshot snapshot = result.Snapshot;

        SystemSummary = result.Identity.Describe(snapshot);
        FixesBlocked = _advisor.ShouldBlockFixes(snapshot);
        // La casilla de confirmar la clave solo aparece con BitLocker MEDIDO activo. Con null
        // (no se pudo comprobar), confirmar la clave no cambiaría nada — FixRunner bloquea igual,
        // fallando cerrado — así que pedirla sería mentirle al técnico; el hallazgo
        // «bitlocker.unknown» del reporte ya explica el bloqueo y que conviene re-analizar.
        BitLockerActive = snapshot.BitLockerActive == true;

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

        // Qué va a hacer EXACTAMENTE «Mejorar rendimiento» con el inicio, con nombres. Sin esto el
        // técnico lo descubre recién al apretar el botón — y si la lista blanca no tiene match en
        // este equipo (común: software de oficina que no es Spotify/Discord/Adobe), el botón hace
        // cero y parece que la herramienta no funcionó.
        if (startupPreview is { EnabledTotal: > 0 } preview)
        {
            SoftwareRows.Add(ToRow(ToStartupPreviewFinding(preview)));
        }

        // Saturación próxima: alerta temprana con lo medido hoy (y una única estimación declarada).
        foreach (Finding finding in _saturation.Evaluate(snapshot))
        {
            SoftwareRows.Add(ToRow(finding));
        }

        // Reputación de lo instalado. Sin la lista no se afirma nada: va a «no determinado».
        if (installedPrograms is not null)
        {
            IReadOnlyList<ReputationMatch> matches =
                ReputationEvaluator.Match(installedPrograms, _options.Reputation.KnownBad);

            foreach (Finding finding in ReputationEvaluator.ToFindings(matches))
            {
                SoftwareRows.Add(ToRow(finding));
            }

            _logger.LogInformation(
                "Programas instalados: {Count}. Con mala reputación: {Bad}.",
                installedPrograms.Count, matches.Count);
        }
        else
        {
            UndeterminedRows.Add(new ReportRow(
                "Programas instalados",
                "No se pudo leer la lista de programas, así que no se revisó si hay software con " +
                "mala reputación.",
                null, "info"));
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

        // Qué software conviene en ESTE equipo, según lo medido (RAM, disco, batería).
        foreach (Recommendation rec in _softwareSelection.Advise(snapshot))
        {
            SoftwareRows.Add(new ReportRow(rec.Title, rec.Detail, rec.Evidence?.ToString(), "info"));
        }

        // El estado del equipo como lista de condiciones: la columna «antes».
        if (_checklistBefore is { } checklist)
        {
            foreach (ChecklistItem item in checklist.Items)
            {
                ChecklistRows.Add(ToChecklistRow(item));
            }

            _logger.LogInformation(
                "Checklist inicial: {Pass} en verde, {Fail} en rojo, {Nd} sin determinar.{NewLine}{Items}",
                checklist.PassCount, checklist.FailCount, checklist.NotDeterminedCount,
                Environment.NewLine,
                string.Join(Environment.NewLine, checklist.Items.Select(i =>
                    $"  [{Mark(i.Status)}] {i.Title} — {i.Reason}")));
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
        ResetProgress(RunningCategoryLabel);
        IsBusy = true;
        CurrentScreen = Screen.Repairing;

        ResetCancellation();

        var progress = new Progress<FixProgress>(p =>
        {
            ProgressIsIndeterminate = false;
            ProgressPercent = p.Percent;
            ProgressLabel = p.Title;
            ProgressDetail = p.Detail;
            ProgressStep = $"Paso {p.Step} de {p.Total}";
        });

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
                    BitLockerWasSuspended = _lastProbe.Snapshot.BitLockerActive == true && BitLockerKeyConfirmed,
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

            // La prueba de que funcionó: se vuelve a medir TODO y se muestra el checklist en dos
            // columnas. Sin esta re-medición, «mejoró» sería una promesa, no un dato.
            if (!result.Aborted)
            {
                await VerifyAfterFixesAsync(_cts!.Token);
            }
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
            ProgressDetail = null;
            ProgressStep = null;
        }
    }

    /// <summary>
    /// Re-mide el equipo después de aplicar los arreglos y arma el checklist antes → después.
    /// </summary>
    /// <remarks>
    /// <para>Es la diferencia entre «apliqué 6 cambios» y «el equipo pasó de 4 condiciones en rojo a
    /// 1». Las que persisten en rojo se muestran con su motivo — si es hardware, se dice. Un
    /// re-análisis que falla no inventa mejoras: deja la columna «después» como no determinada.</para>
    ///
    /// <para>Ojo con una limitación medida: el retraso de arranque y el tiempo de boot NO cambian
    /// hasta reiniciar, así que esas condiciones van a salir iguales acá. El texto de reinicio ya lo
    /// avisa; la comparación de boot real es la fase 3 del plan.</para>
    /// </remarks>
    private async Task VerifyAfterFixesAsync(CancellationToken ct)
    {
        if (_checklistBefore is not { } before)
        {
            return;
        }

        ProgressLabel = "Verificando el resultado…";
        ProgressDetail = "Se vuelve a medir el equipo para comparar contra el estado inicial.";
        ProgressIsIndeterminate = true;

        try
        {
            Task<ConnectivityResult> networkTask = _connectivity.CheckAsync(ct);
            ProbeResult fresh = await _probe.ProbeAsync(progress: null, ct);
            ConnectivityResult network = await networkTask;

            fresh = fresh with { Snapshot = fresh.Snapshot with { Network = network.Status } };

            ChecklistReport after = ChecklistEvaluator.Evaluate(
                fresh.Snapshot, fresh.Crash, fresh.Failures, _options.Thresholds);

            ChecklistCompareRows.Clear();
            var pair = new BeforeAfterChecklist(before, after);

            foreach ((ChecklistItem b, ChecklistItem? a) in pair.Rows)
            {
                ChecklistCompareRows.Add(ToCompareRow(b, a));
            }

            OnPropertyChanged(nameof(HasChecklistCompareRows));

            // El snapshot fresco pasa a ser el vigente: una segunda pasada de fixes decide sobre
            // el estado real, no sobre el de antes de reparar.
            _lastProbe = fresh;
            _lastCrash = CrashAnalyzer.Analyze(fresh.Crash);

            _logger.LogInformation(
                "Checklist después de reparar: {Improved} a verde, {Still} en rojo " +
                "({Regressed} empeoraron), {Lost} salieron del rojo sin re-medirse.{NewLine}{Items}",
                pair.ImprovedCount, pair.StillFailingCount, pair.RegressedCount, pair.LostEvidenceCount,
                Environment.NewLine,
                string.Join(Environment.NewLine, pair.Rows.Select(r =>
                    $"  {r.Before.Title}: {Mark(r.Before.Status)} → {Mark(r.After?.Status ?? ChecklistStatus.NotDetermined)}")));

            // Una regresión no puede quedar enterrada en el log: si algo EMPEORÓ tras reparar, es
            // lo primero que el técnico tiene que ver.
            if (pair.RegressedCount > 0)
            {
                SetStatus(
                    $"Atención: {pair.RegressedCount} condición(es) que estaban bien ahora están mal. " +
                    "Revisá la tabla antes/después y considerá «Deshacer todo».",
                    warning: true);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("La verificación posterior se canceló.");
        }
        catch (Exception ex)
        {
            // La reparación ya está hecha y registrada; si la verificación falla, se dice y ya.
            _logger.LogError(ex, "La verificación posterior falló.");
            SetStatus(
                "Los cambios se aplicaron, pero la re-medición para comparar antes/después falló: " +
                $"{ex.GetType().Name}. Volvé a «Analizar el equipo» para ver el estado actual.",
                warning: true);
        }
        finally
        {
            ProgressLabel = string.Empty;
            ProgressDetail = null;
            ProgressIsIndeterminate = false;
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
            OnPropertyChanged(nameof(HasRepairRows));
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
        OnPropertyChanged(nameof(HasRepairRows));
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

        ResetProgress("Instalando programas");

        // Antes acá iba CancellationToken.None: instalar programas era la ÚNICA operación larga
        // de la app que no se podía cancelar, y es la que más tarda (descarga + instalador por
        // paquete). El técnico quedaba mirando una barra sin forma de salir salvo matar la app.
        ResetCancellation();

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressIsIndeterminate = false;

            // El paquete en curso cuenta como medio hecho: así la barra se mueve mientras descarga,
            // en vez de saltar de golpe al terminar cada uno.
            ProgressPercent = Math.Clamp(
                100.0 * (p.Index - 1 + (p.Finished is null ? 0.5 : 1.0)) / p.Total, 0, 100);
            ProgressStep = $"{p.Index} de {p.Total} programas";
            ProgressLabel = p.Finished is null
                ? $"Instalando {p.DisplayName}…"
                : $"{p.DisplayName}: {DescribeOutcome(p.Finished)}";

            InstallProgressLabel = p.Finished is null
                ? $"Descargando e instalando {p.DisplayName}…"
                : null;

            if (!byId.TryGetValue(p.PackageId, out AppChoice? app)) { return; }

            if (p.Finished is null) { app.MarkInstalling(); }
            else { app.MarkResult(p.Finished); }
        });

        try
        {
            IReadOnlyList<WingetResult> results = await _winget.InstallAsync(
                selected.Select(a => a.Package).ToList(), progress, _cts!.Token);

            InstallProgressLabel = null;

            // Si esta corrida tuvo que instalar winget primero, que quede dicho en el resumen
            // final — no solo en el log. Cuando el paquete en curso termina bien, su fila no
            // muestra ningún detalle (el motivo solo se ve ante un fallo), así que sin esto el
            // técnico nunca se enteraba de que la app instaló algo además de lo que había pedido.
            string summary = _winget.LastBootstrapNote is { } note
                ? $"{note} {WingetResultParser.Summarize(results)}"
                : WingetResultParser.Summarize(results);

            SetStatus(summary, warning: results.Any(r => !r.PackageAvailable));

            foreach (WingetResult failed in results.Where(r => !r.PackageAvailable))
            {
                _logger.LogWarning("{PackageId}: {Detail}", failed.PackageId, failed.Detail);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelar no es un fallo. Lo que ya se instaló quedó instalado — un instalador que
            // ya arrancó termina igual; lo que se corta es la tanda, no el paquete en curso.
            _logger.LogInformation("La instalación se canceló.");
            InstallProgressLabel = null;
            SetStatus(
                "Instalación cancelada. Lo que ya se había instalado quedó instalado; el resto " +
                "de la lista no se intentó.",
                warning: true);
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
            ProgressLabel = string.Empty;
            ProgressStep = null;
        }
    }

    /// <summary>
    /// Abre la pantalla de Windows donde se eligen las aplicaciones predeterminadas.
    /// </summary>
    /// <remarks>
    /// <para><b>Por qué no se hace solo, que es lo que uno querría.</b> Windows bloquea
    /// deliberadamente que un programa cambie el navegador o el lector de PDF predeterminados: el
    /// valor va firmado con un hash por usuario+equipo que Microsoft no documenta, y desde las
    /// actualizaciones de febrero de 2024 hay además un driver (<c>UCPD.sys</c>, User Choice
    /// Protection Driver) que impide escribir esas claves del registro aunque se tenga el hash
    /// correcto.</para>
    ///
    /// <para>Las herramientas que lo logran igual (SetUserFTA y similares) necesitan
    /// <b>desactivar ese driver</b> primero. Eso es exactamente lo que el «Tier C — NO construir»
    /// de <c>docs/PLAN.md</c> prohíbe: pelearse con una protección de Windows, con el agregado de
    /// que se rompe en cada actualización grande y dejaría a EasyFix haciendo algo que un
    /// antivirus razonablemente marca como sospechoso.</para>
    ///
    /// <para>Entonces se hace lo honesto: llevar al técnico al lugar exacto en dos clics, en vez
    /// de prometer una automatización que Windows no permite.</para>
    /// </remarks>
    private void OpenDefaultApps()
    {
        try
        {
            // ms-settings: es el esquema oficial de Windows para abrir Configuración en una
            // página concreta. UseShellExecute = true es obligatorio para que el shell resuelva
            // el esquema; es la única invocación de la app que lo usa, y no lleva ningún dato
            // variable: la URI es una constante, no se concatena nada del equipo.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true,
            });

            SetStatus(
                "Se abrió la pantalla de aplicaciones predeterminadas de Windows. Buscá «Google " +
                "Chrome» y tocá «Establecer como predeterminado» para el navegador, y «Adobe " +
                "Acrobat Reader» para «.pdf». Windows no permite que un programa haga este cambio " +
                "solo: lo bloquea a propósito desde 2024.",
                warning: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo abrir la configuración de aplicaciones predeterminadas.");
            SetStatus(
                "No se pudo abrir la pantalla de Configuración. Abrila a mano: Inicio → " +
                "Configuración → Aplicaciones → Aplicaciones predeterminadas.",
                warning: true);
        }
    }

    /// <summary>
    /// Muestra dónde está winget, su versión y si puede leer su catálogo. Solo lectura.
    /// </summary>
    /// <remarks>
    /// Existe por el fallo más difícil de diagnosticar del módulo: winget corriendo elevado no ve su
    /// propio catálogo, porque se instala por usuario. Sin este botón, el técnico solo veía «Falló».
    /// <para>
    /// Desde que <see cref="WingetService"/> intenta instalar winget solo cuando falta (ver
    /// <c>IWingetBootstrapper</c>), este botón ya no es el primer paso — «Instalar» ya lo intenta
    /// implícito. Queda para el caso en que el bootstrap automático no alcanzó (ej. falta
    /// Microsoft.UI.Xaml, que no se baja de un origen oficial estable) y hace falta el detalle
    /// técnico para resolverlo a mano.
    /// </para>
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

    /// <summary>
    /// Traduce la vista previa de inicio a un hallazgo concreto, con nombres. Es la respuesta a
    /// «¿qué va a hacer el botón?» antes de apretarlo.
    /// </summary>
    private static Finding ToStartupPreviewFinding(StartupPreview preview)
    {
        var metrics = new List<Metric>();
        var parts = new List<string>();

        // El dominio bloquea el fix ENTERO antes de clasificar nada (ver StartupPreview.Build):
        // si pasó esto, ninguna de las otras tres listas tiene nada, así que se corta acá con el
        // motivo real en vez de mostrar "0 se desactivan solos" sin explicación.
        if (preview.BlockedByDomain.Count > 0)
        {
            return new Finding(
                "startup.preview",
                Severity.Info,
                "«Mejorar rendimiento» no va a tocar el inicio en este equipo",
                $"Hay {preview.BlockedByDomain.Count} programa(s) de inicio que en un equipo sin " +
                "dominio se podrían evaluar para desactivar, pero acá no: el equipo está en un " +
                "dominio (o no se pudo comprobar), y en ese caso EasyFix no toca los programas de " +
                "inicio aunque estén en la lista de seguros — las políticas de la empresa los " +
                "reescribirían en la próxima actualización de directivas.",
                new[] { new Metric("Afectados", string.Join(", ", preview.BlockedByDomain)) });
        }

        if (preview.AutoDisable.Count > 0)
        {
            metrics.Add(new Metric("Se desactivan solos", string.Join(", ", preview.AutoDisable)));
            parts.Add(
                $"«Mejorar rendimiento» va a intentar desactivar {preview.AutoDisable.Count} sin " +
                $"preguntar nada más: {string.Join(", ", preview.AutoDisable)}.");
        }

        if (preview.NeedsApproval.Count > 0)
        {
            metrics.Add(new Metric("Piden tu confirmación", string.Join(", ", preview.NeedsApproval)));
            parts.Add(
                $"{preview.NeedsApproval.Count} más podrían desactivarse pero no están en la lista " +
                $"de seguros, así que «Mejorar rendimiento» NO los toca: {string.Join(", ", preview.NeedsApproval)}. " +
                "Si los conocés, podés desactivarlos ahora mismo desde el Administrador de tareas → pestaña Inicio.");
        }

        if (preview.Blocked.Count > 0)
        {
            parts.Add(
                $"{preview.Blocked.Count} son intocables (seguridad, driver o componente protegido): " +
                $"{string.Join(", ", preview.Blocked)}.");
        }

        parts.Add(
            "Esto cubre los programas que arrancan por el registro de Windows (Run); los accesos " +
            "directos en la carpeta de inicio todavía no los toca ningún fix, aunque el diagnóstico " +
            "los cuente aparte.");

        return new Finding(
            "startup.preview",
            Severity.Info,
            preview.AutoDisable.Count > 0
                ? $"«Mejorar» va a intentar desactivar {preview.AutoDisable.Count} programa(s) de inicio"
                : "Ningún programa de inicio se desactiva solo en este equipo",
            string.Join(" ", parts),
            metrics);
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
        OnPropertyChanged(nameof(HasChecklistRows));
        OnPropertyChanged(nameof(HasChecklistCompareRows));
    }

    // ---- Checklist en pantalla ----------------------------------------------------------------

    private static string Mark(ChecklistStatus status) => status switch
    {
        ChecklistStatus.Pass => "✓",
        ChecklistStatus.Fail => "✗",
        _ => "—",
    };

    private static string KindFor(ChecklistItem item) => item.Status switch
    {
        ChecklistStatus.Pass => "ok",
        ChecklistStatus.Fail => item.NeedsHardware ? "crit" : "warn",
        _ => "info",
    };

    /// <summary>Una condición como fila simple: «✓ Salud del disco» + el motivo medido.</summary>
    private static ReportRow ToChecklistRow(ChecklistItem item) => new(
        $"{Mark(item.Status)}  {item.Title}",
        item.Reason,
        null,
        KindFor(item));

    /// <summary>Una condición como «antes → después», emparejada por id.</summary>
    private static ReportRow ToCompareRow(ChecklistItem before, ChecklistItem? after) => new(
        after is null ? before.Title : $"{Mark(after.Status)}  {before.Title}",
        after?.Reason ?? before.Reason,
        $"antes {Mark(before.Status)}  →  después {Mark(after?.Status ?? ChecklistStatus.NotDetermined)}",
        after is null ? "info" : KindFor(after));

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
