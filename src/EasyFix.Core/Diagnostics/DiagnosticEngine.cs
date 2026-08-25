using System.Diagnostics;
using EasyFix.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Diagnostics;

/// <summary>
/// Corre los chequeos de diagnóstico en paralelo, con timeout por chequeo.
/// </summary>
/// <remarks>
/// <para><b>Paralelo con tope.</b> Cada consulta WMI cuesta entre 100 y 500 ms; treinta en serie son
/// quince segundos de UI congelada. En paralelo con un tope de ~8 el escaneo entra en el presupuesto
/// de 10 s. El tope existe porque lanzar treinta consultas WMI a la vez satura el proveedor y termina
/// siendo más lento.</para>
///
/// <para><b>Timeout por chequeo, no global.</b> Un chequeo colgado —pasa con SMART en discos que
/// están fallando, justo el caso que más importa— no puede impedir que se emita el reporte. Se marca
/// como no determinado y el escaneo sigue.</para>
///
/// <para><b>Un chequeo que revienta no tumba el escaneo.</b> Se atrapa todo a propósito: preferimos
/// veintinueve hallazgos y un error listado, a cero hallazgos y un stack trace.</para>
/// </remarks>
public sealed class DiagnosticEngine
{
    private readonly IReadOnlyList<IDiagnosticCheck> _checks;
    private readonly ThresholdOptions _thresholds;
    private readonly TimeProvider _time;
    private readonly ILogger<DiagnosticEngine> _logger;

    public DiagnosticEngine(
        IEnumerable<IDiagnosticCheck> checks,
        ThresholdOptions thresholds,
        TimeProvider time,
        ILogger<DiagnosticEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _checks = checks.ToList();
        _thresholds = thresholds;
        _time = time;
        _logger = logger;
    }

    /// <summary>Escaneo rápido: solo WMI, registro y Event Log. Objetivo, menos de 10 s.</summary>
    public Task<ScanReport> RunQuickScanAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RunAsync(_checks.Where(c => !c.IsDeepScan).ToList(), progress, ct);

    /// <summary>
    /// Escaneo profundo: DISM, chkdsk, SFC. Minutos. Vive detrás de "Reparar errores" y nunca corre
    /// al abrir la app.
    /// </summary>
    public Task<ScanReport> RunDeepScanAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RunAsync(_checks.Where(c => c.IsDeepScan).ToList(), progress, ct);

    private async Task<ScanReport> RunAsync(
        IReadOnlyList<IDiagnosticCheck> checks,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        long startTicks = _time.GetTimestamp();

        var findings = new List<Finding>();
        var failures = new List<CheckFailure>();
        var gate = new SemaphoreSlim(Math.Max(1, _thresholds.MaxConcurrentChecks));

        // Los dos List no son thread-safe: se sincroniza el agregado, que es lo único compartido.
        var sync = new object();

        IEnumerable<Task> tasks = checks.Select(async check =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                progress?.Report(check.DisplayName);

                (Finding? finding, CheckFailure? failure) = await RunOneAsync(check, ct).ConfigureAwait(false);

                lock (sync)
                {
                    if (finding is not null)
                    {
                        findings.Add(finding);
                    }

                    if (failure is not null)
                    {
                        failures.Add(failure);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        TimeSpan duration = _time.GetElapsedTime(startTicks);

        if (duration > TimeSpan.FromSeconds(_thresholds.QuickScanBudgetSeconds) && checks.All(c => !c.IsDeepScan))
        {
            // No es un error, es una señal: si el escaneo rápido se pasa del presupuesto, algún
            // chequeo se volvió lento y hay que sacarlo de la ruta rápida.
            _logger.LogWarning(
                "El escaneo rápido tardó {Elapsed:0.0} s, por encima del presupuesto de {Budget} s.",
                duration.TotalSeconds, _thresholds.QuickScanBudgetSeconds);
        }

        _logger.LogInformation(
            "Escaneo completo en {Elapsed:0.0} s: {Findings} hallazgos, {Failures} sin determinar.",
            duration.TotalSeconds, findings.Count, failures.Count);

        return new ScanReport(Order(findings), failures.OrderBy(f => f.CheckId, StringComparer.Ordinal).ToList(), duration);
    }

    private async Task<(Finding?, CheckFailure?)> RunOneAsync(IDiagnosticCheck check, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_thresholds.PerCheckTimeout);

        try
        {
            Finding? finding = await check.RunAsync(timeoutCts.Token).ConfigureAwait(false);
            return (finding, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación del usuario: aborta el escaneo completo. Va explícito y ANTES del catch
            // general, porque OperationCanceledException también es Exception: sin esta cláusula el
            // catch de abajo se la comía y el escaneo devolvía un reporte lleno de "no se pudo
            // determinar" en vez de abortar. Apretar Cancelar tiene que cancelar.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout del chequeo, no cancelación del usuario.
            _logger.LogWarning(
                "El chequeo {CheckId} excedió {Timeout} y se marcó como no determinado.",
                check.Id, _thresholds.PerCheckTimeout);

            return (null, new CheckFailure(
                check.Id,
                $"No se pudo determinar: tardó más de {_thresholds.PerCheckTimeout.TotalSeconds:0} s.",
                TimedOut: true));
        }
        catch (Exception ex)
        {
            // A propósito se atrapa todo: un chequeo roto no puede dejar al técnico sin reporte.
            _logger.LogError(ex, "El chequeo {CheckId} falló.", check.Id);

            return (null, new CheckFailure(
                check.Id,
                $"No se pudo determinar: {ex.GetType().Name}: {ex.Message}",
                TimedOut: false));
        }
    }

    /// <summary>
    /// Ordena por gravedad descendente y después por id, para que el reporte sea estable entre
    /// corridas: el paralelismo hace que el orden de llegada sea aleatorio.
    /// </summary>
    private static List<Finding> Order(List<Finding> findings) =>
        findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();
}
