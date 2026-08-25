using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

public sealed class DiagnosticEngineTests
{
    /// <summary>Chequeo configurable: puede devolver, devolver null, colgarse o reventar.</summary>
    private sealed class FakeCheck : IDiagnosticCheck
    {
        private readonly Func<CancellationToken, Task<Finding?>> _body;

        public FakeCheck(
            string id,
            Func<CancellationToken, Task<Finding?>> body,
            bool isDeepScan = false)
        {
            Id = id;
            DisplayName = id;
            IsDeepScan = isDeepScan;
            _body = body;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool IsDeepScan { get; }
        public int RunCount { get; private set; }

        public Task<Finding?> RunAsync(CancellationToken ct)
        {
            RunCount++;
            return _body(ct);
        }

        public static FakeCheck Returning(string id, Severity severity, bool deep = false) =>
            new(id, _ => Task.FromResult<Finding?>(
                new Finding(id, severity, $"Título {id}", $"Detalle {id}")), deep);

        public static FakeCheck Clean(string id, bool deep = false) =>
            new(id, _ => Task.FromResult<Finding?>(null), deep);

        public static FakeCheck Throwing(string id, Exception ex) =>
            new(id, _ => Task.FromException<Finding?>(ex));

        /// <summary>Se cuelga hasta que lo cancelen: simula SMART en un disco que está fallando.</summary>
        public static FakeCheck Hanging(string id) =>
            new(id, async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            });
    }

    private static DiagnosticEngine Build(
        IEnumerable<IDiagnosticCheck> checks,
        ThresholdOptions? thresholds = null) =>
        new(checks,
            thresholds ?? new ThresholdOptions { PerCheckTimeoutSeconds = 1, MaxConcurrentChecks = 4 },
            TimeProvider.System,
            NullLogger<DiagnosticEngine>.Instance);

    [Fact]
    public async Task ChequeoLimpio_NoGeneraHallazgo()
    {
        ScanReport report = await Build(new[] { FakeCheck.Clean("a") }).RunQuickScanAsync();

        Assert.Empty(report.Findings);
        Assert.Empty(report.Failures);
        Assert.False(report.IsPartial);
    }

    [Fact]
    public async Task LosHallazgosSeOrdenanPorGravedad()
    {
        // El paralelismo hace que el orden de llegada sea aleatorio: el reporte tiene que ser estable.
        var checks = new IDiagnosticCheck[]
        {
            FakeCheck.Returning("z-info", Severity.Info),
            FakeCheck.Returning("a-warning", Severity.Warning),
            FakeCheck.Returning("m-critical", Severity.Critical),
        };

        ScanReport report = await Build(checks).RunQuickScanAsync();

        Assert.Equal(new[] { "m-critical", "a-warning", "z-info" }, report.Findings.Select(f => f.CheckId));
        Assert.True(report.HasCritical);
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public async Task MismosChequeos_MismoOrden_EnCorridasSucesivas()
    {
        var checks = Enumerable.Range(0, 12)
            .Select(i => FakeCheck.Returning($"check-{i:D2}", Severity.Warning))
            .Cast<IDiagnosticCheck>()
            .ToList();

        ScanReport first = await Build(checks).RunQuickScanAsync();
        ScanReport second = await Build(checks).RunQuickScanAsync();

        Assert.Equal(first.Findings.Select(f => f.CheckId), second.Findings.Select(f => f.CheckId));
    }

    [Fact]
    public async Task ChequeoQueRevienta_SeReportaComoNoDeterminado_YElRestoSigue()
    {
        var checks = new IDiagnosticCheck[]
        {
            FakeCheck.Throwing("roto", new InvalidOperationException("WMI no responde")),
            FakeCheck.Returning("sano", Severity.Warning),
        };

        ScanReport report = await Build(checks).RunQuickScanAsync();

        // Preferimos un hallazgo y un error listado, a cero hallazgos y un stack trace.
        Assert.Single(report.Findings);
        Assert.Equal("sano", report.Findings[0].CheckId);

        CheckFailure failure = Assert.Single(report.Failures);
        Assert.Equal("roto", failure.CheckId);
        Assert.False(failure.TimedOut);
        Assert.Contains("WMI no responde", failure.Reason, StringComparison.Ordinal);
        Assert.True(report.IsPartial);
    }

    [Fact]
    public async Task ChequeoColgado_SeCortaPorTimeout_YElReporteSeEmite()
    {
        // Es el caso real: SMART se cuelga justo en los discos que están fallando.
        var checks = new IDiagnosticCheck[]
        {
            FakeCheck.Hanging("smart"),
            FakeCheck.Returning("otro", Severity.Info),
        };

        var fast = new ThresholdOptions { PerCheckTimeoutSeconds = 1, MaxConcurrentChecks = 4 };

        ScanReport report = await Build(checks, fast).RunQuickScanAsync();

        Assert.Single(report.Findings);
        CheckFailure failure = Assert.Single(report.Failures);
        Assert.Equal("smart", failure.CheckId);
        Assert.True(failure.TimedOut);
        Assert.Contains("No se pudo determinar", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscaneoRapido_NoCorreLosChequeosProfundos()
    {
        // DISM /ScanHealth tarda entre 5 y 15 minutos. Si se cuela en la ruta rápida, la app parece
        // colgada al abrirse.
        var deep = FakeCheck.Returning("dism", Severity.Warning, deep: true);
        var quick = FakeCheck.Returning("wmi", Severity.Warning);

        ScanReport report = await Build(new IDiagnosticCheck[] { deep, quick }).RunQuickScanAsync();

        Assert.Single(report.Findings);
        Assert.Equal("wmi", report.Findings[0].CheckId);
        Assert.Equal(0, deep.RunCount);
        Assert.Equal(1, quick.RunCount);
    }

    [Fact]
    public async Task EscaneoProfundo_SoloCorreLosProfundos()
    {
        var deep = FakeCheck.Returning("dism", Severity.Warning, deep: true);
        var quick = FakeCheck.Returning("wmi", Severity.Warning);

        ScanReport report = await Build(new IDiagnosticCheck[] { deep, quick }).RunDeepScanAsync();

        Assert.Single(report.Findings);
        Assert.Equal("dism", report.Findings[0].CheckId);
        Assert.Equal(0, quick.RunCount);
    }

    [Fact]
    public async Task RespetaElTopeDeConcurrencia()
    {
        // Lanzar treinta consultas WMI a la vez satura el proveedor y sale más lento.
        const int Concurrency = 3;
        int running = 0;
        int peak = 0;
        var sync = new object();

        var checks = Enumerable.Range(0, 12).Select(i => new FakeCheck($"c{i:D2}", async ct =>
        {
            lock (sync)
            {
                running++;
                peak = Math.Max(peak, running);
            }

            await Task.Delay(20, ct);

            lock (sync)
            {
                running--;
            }

            return null;
        })).Cast<IDiagnosticCheck>().ToList();

        await Build(checks, new ThresholdOptions
        {
            PerCheckTimeoutSeconds = 5,
            MaxConcurrentChecks = Concurrency,
        }).RunQuickScanAsync();

        Assert.True(peak <= Concurrency, $"Corrieron {peak} chequeos a la vez; el tope era {Concurrency}.");
        Assert.True(peak > 1, "No hubo paralelismo real: el escaneo sería serial y demasiado lento.");
    }

    [Fact]
    public async Task ConcurrenciaCeroONegativa_SeTrataComoUno_SinDeadlock()
    {
        // Una config corrupta no puede colgar la app.
        var engine = Build(
            new[] { FakeCheck.Returning("a", Severity.Info) },
            new ThresholdOptions { PerCheckTimeoutSeconds = 1, MaxConcurrentChecks = 0 });

        ScanReport report = await engine.RunQuickScanAsync();

        Assert.Single(report.Findings);
    }

    [Fact]
    public async Task InformaElProgreso()
    {
        var reported = new List<string>();
        var progress = new Progress<string>(reported.Add);

        await Build(new[] { FakeCheck.Clean("disco"), FakeCheck.Clean("memoria") })
            .RunQuickScanAsync(progress);

        // Progress<T> despacha de forma asíncrona; se espera a que lleguen los dos.
        for (int i = 0; i < 50 && reported.Count < 2; i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("disco", reported);
        Assert.Contains("memoria", reported);
    }

    [Fact]
    public async Task CancelacionDelUsuario_SePropaga_YNoSeConfundeConTimeout()
    {
        using var cts = new CancellationTokenSource();
        var engine = Build(new[] { FakeCheck.Hanging("lento") });

        Task<ScanReport> run = engine.RunQuickScanAsync(null, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task SinChequeos_DevuelveReporteVacio()
    {
        ScanReport report = await Build(Array.Empty<IDiagnosticCheck>()).RunQuickScanAsync();

        Assert.Empty(report.Findings);
        Assert.Empty(report.Failures);
        Assert.False(report.HasCritical);
    }

    [Fact]
    public async Task LasFallasSeOrdenanDeFormaEstable()
    {
        var checks = new IDiagnosticCheck[]
        {
            FakeCheck.Throwing("zeta", new InvalidOperationException("x")),
            FakeCheck.Throwing("alfa", new InvalidOperationException("y")),
        };

        ScanReport report = await Build(checks).RunQuickScanAsync();

        Assert.Equal(new[] { "alfa", "zeta" }, report.Failures.Select(f => f.CheckId));
    }
}
