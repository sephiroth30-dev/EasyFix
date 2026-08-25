using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Recommendations;
using Microsoft.Extensions.Logging;

namespace EasyFix.App.ViewModels;

/// <summary>Qué panel se está mostrando.</summary>
public enum Screen
{
    Home,
    Scanning,
    Report,
    Apps,
}

/// <summary>Una línea del reporte, ya lista para mostrar.</summary>
/// <param name="Title">Título.</param>
/// <param name="Detail">Explicación.</param>
/// <param name="Metrics">Los números, en una línea.</param>
/// <param name="Kind">Determina el color de la franja: <c>crit</c>, <c>warn</c> o <c>info</c>.</param>
public sealed record ReportRow(string Title, string Detail, string? Metrics, string Kind);

/// <summary>
/// Pantalla principal. Sin lógica de negocio: orquesta la sonda, las reglas y el motor de
/// recomendaciones, todos de <c>EasyFix.Core</c>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly EasyFixOptions _options;
    private readonly SystemProbe _probe;
    private readonly HardwareAdvisor _advisor;
    private readonly WingetService _winget;
    private readonly ILogger<MainViewModel> _logger;

    private CancellationTokenSource? _scanCts;

    public MainViewModel(
        EasyFixOptions options,
        SystemProbe probe,
        HardwareAdvisor advisor,
        WingetService winget,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(advisor);
        ArgumentNullException.ThrowIfNull(winget);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _probe = probe;
        _advisor = advisor;
        _winget = winget;
        _logger = logger;

        foreach (WingetPackage package in options.WingetPackages)
        {
            Apps.Add(new AppChoice(package));
        }

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync);
        CancelCommand = new RelayCommand(() => _scanCts?.Cancel());
        BackCommand = new RelayCommand(() => CurrentScreen = Screen.Home);

        RepairCommand = new RelayCommand(() => SetStatus(
            "«Reparar errores» todavía no está implementado. Faltan las llamadas a DISM, SFC y chkdsk.",
            warning: true));

        InstallAppsCommand = new RelayCommand(() =>
        {
            StatusMessage = null;
            CurrentScreen = Screen.Apps;
        });

        RunInstallCommand = new AsyncRelayCommand(InstallSelectedAsync, () => !IsInstalling);

        ApplyFixesCommand = new RelayCommand(() => SetStatus(
            "Todavía no se aplica ningún cambio: falta el servicio de punto de restauración. " +
            "Sin un punto de restauración verificado, la app no toca nada. Este diagnóstico es de " +
            "solo lectura.",
            warning: true));
    }

    public IAsyncRelayCommand AnalyzeCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand RepairCommand { get; }
    public IRelayCommand InstallAppsCommand { get; }
    public IAsyncRelayCommand RunInstallCommand { get; }
    public IRelayCommand ApplyFixesCommand { get; }

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
    private string _scanCounter = string.Empty;

    /// <summary>Lo que se puede arreglar sin comprar nada.</summary>
    public ObservableCollection<ReportRow> SoftwareRows { get; } = new();

    /// <summary>Lo que necesita comprar algo o abrir el equipo.</summary>
    public ObservableCollection<ReportRow> HardwareRows { get; } = new();

    /// <summary>Lo que no se pudo medir. Se muestra: un chequeo que falló no es "todo bien".</summary>
    public ObservableCollection<ReportRow> UndeterminedRows { get; } = new();

    public bool HasSoftwareRows => SoftwareRows.Count > 0;
    public bool HasHardwareRows => HardwareRows.Count > 0;
    public bool HasUndeterminedRows => UndeterminedRows.Count > 0;

    /// <summary><c>true</c> cuando el disco está fallando: se bloquea todo lo demás.</summary>
    [ObservableProperty]
    private bool _fixesBlocked;

    /// <summary>Los programas configurados en appsettings.json, con su casilla.</summary>
    public ObservableCollection<AppChoice> Apps { get; } = new();

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string? _installProgressLabel;

    partial void OnIsInstallingChanged(bool value) => RunInstallCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Instala los programas marcados. Cada uno se descarga del repositorio oficial de Microsoft en
    /// el momento, así que siempre entra la última versión publicada.
    /// </summary>
    private async Task InstallSelectedAsync()
    {
        List<AppChoice> selected = Apps.Where(a => a.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SetStatus("No hay ningún programa marcado.", warning: false);
            return;
        }

        IsInstalling = true;
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
            IsInstalling = false;
        }
    }

    private async Task AnalyzeAsync()
    {
        SoftwareRows.Clear();
        HardwareRows.Clear();
        UndeterminedRows.Clear();
        StatusMessage = null;
        ProgressLabel = "Iniciando…";
        ScanCounter = string.Empty;
        CurrentScreen = Screen.Scanning;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        var progress = new Progress<string>(label => ProgressLabel = label);

        try
        {
            ProbeResult result = await _probe.ProbeAsync(progress, _scanCts.Token);
            BuildReport(result);
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
            // Nada de tragarse el error: en el equipo de un cliente hay que saber qué pasó.
            _logger.LogError(ex, "El análisis falló.");
            CurrentScreen = Screen.Home;
            SetStatus($"El análisis falló: {ex.GetType().Name}: {ex.Message}", warning: true);
        }
    }

    private void BuildReport(ProbeResult result)
    {
        SystemSnapshot snapshot = result.Snapshot;

        SystemSummary = result.Identity.Describe(snapshot);
        FixesBlocked = _advisor.ShouldBlockFixes(snapshot);

        foreach (Finding finding in SoftwareFindings.Evaluate(snapshot, _options.Thresholds))
        {
            SoftwareRows.Add(new ReportRow(
                finding.Title,
                finding.Detail,
                Join(finding.AllMetrics),
                finding.Severity switch
                {
                    Severity.Critical => "crit",
                    Severity.Warning => "warn",
                    _ => "info",
                }));
        }

        // Las recomendaciones se reparten por si hace falta comprar algo. Es la división que hace
        // honesto al reporte: "puedo limpiar esto" y "hay que comprar un SSD" no van en la misma lista.
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
                DescribeProbe(failure.CheckId),
                failure.Reason,
                null,
                "info"));
        }

        OnPropertyChanged(nameof(HasSoftwareRows));
        OnPropertyChanged(nameof(HasHardwareRows));
        OnPropertyChanged(nameof(HasUndeterminedRows));

        ScanCounter = $"{SoftwareRows.Count + HardwareRows.Count} hallazgos";

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

    private static string Join(IReadOnlyList<Metric> metrics) =>
        string.Join("  ·  ", metrics.Select(m => m.ToString()));

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
        _ => probeId,
    };

    private void SetStatus(string message, bool warning)
    {
        StatusIsWarning = warning;
        StatusMessage = message;
    }
}
