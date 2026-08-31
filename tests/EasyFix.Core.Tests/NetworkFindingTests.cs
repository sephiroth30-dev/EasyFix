using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Network;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El aviso de «sin internet» en el reporte.
/// </summary>
/// <remarks>
/// Existe para que el técnico se entere <b>antes</b> de apretar «Instalar programas». En el equipo del
/// 2026-08-29 se enteró al revés: probó los cuatro programas, fallaron los cuatro, y el log le echó la
/// culpa al catálogo de winget.
/// </remarks>
public sealed class NetworkFindingTests
{
    private static IReadOnlyList<Finding> Evaluate(ConnectivityStatus? status) =>
        SoftwareFindings.Evaluate(
            new SystemSnapshot { Network = status },
            new ThresholdOptions());

    private static Finding? NetworkFinding(ConnectivityStatus? status) =>
        Evaluate(status).FirstOrDefault(f => f.CheckId == "network.offline");

    [Fact]
    public void ConInternet_NoHayAviso() =>
        Assert.Null(NetworkFinding(ConnectivityStatus.Online));

    [Fact]
    public void SinComprobar_NoHayAviso()
    {
        // null = no se midió. No se afirma que haya internet NI que falte: es la misma regla que el
        // resto del diagnóstico. Inventar un aviso sobre un dato ausente es lo que hace desconfiar
        // del reporte entero.
        Assert.Null(NetworkFinding(null));
    }

    [Theory]
    [InlineData(ConnectivityStatus.NoAdapter)]
    [InlineData(ConnectivityStatus.DnsFailed)]
    [InlineData(ConnectivityStatus.Unreachable)]
    [InlineData(ConnectivityStatus.CaptivePortal)]
    [InlineData(ConnectivityStatus.Unknown)]
    public void CadaEstadoSinConexion_GeneraUnAviso(ConnectivityStatus status)
    {
        Finding? finding = NetworkFinding(status);

        Assert.NotNull(finding);
        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Theory]
    [InlineData(ConnectivityStatus.NoAdapter)]
    [InlineData(ConnectivityStatus.DnsFailed)]
    [InlineData(ConnectivityStatus.Unreachable)]
    [InlineData(ConnectivityStatus.CaptivePortal)]
    [InlineData(ConnectivityStatus.Unknown)]
    public void CadaEstadoTieneSuPropioTextoYSuPropiaMetrica(ConnectivityStatus status)
    {
        // Cinco motivos distintos llevan a cinco acciones distintas del técnico. Un «sin conexión» a
        // secas manda a revisar el cable cuando el problema puede ser un portal cautivo.
        Finding? finding = NetworkFinding(status);

        Assert.NotNull(finding);
        Assert.NotEmpty(finding.Title);

        Assert.NotNull(finding.Metrics);
        Metric metric = Assert.Single(finding.Metrics);
        Assert.NotEqual(status.ToString(), metric.Value);   // traducido, no el enum crudo
    }

    [Fact]
    public void LosCincoAvisosSonDistintosEntreSi()
    {
        var titles = new[]
        {
            ConnectivityStatus.NoAdapter,
            ConnectivityStatus.DnsFailed,
            ConnectivityStatus.Unreachable,
            ConnectivityStatus.CaptivePortal,
            ConnectivityStatus.Unknown,
        }.Select(s => NetworkFinding(s)!.Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ElAvisoDiceQueInstalarNoVaAFuncionar()
    {
        Finding finding = NetworkFinding(ConnectivityStatus.DnsFailed)!;

        Assert.Contains("Instalar programas", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ElAvisoAclaraQueElRestoSiFunciona()
    {
        // Importa tanto como el aviso: sin esta frase, «sin internet» se lee como «la herramienta no
        // sirve en este equipo», y analizar, mejorar y reparar funcionan igual sin conexión.
        Finding finding = NetworkFinding(ConnectivityStatus.NoAdapter)!;

        Assert.Contains("sin internet", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reparar errores", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSeExplicaComoNoComprobado_NoComoSinRed()
    {
        // Decir «no hay red» cuando lo que pasó es que la comprobación falló es afirmar algo no medido.
        Finding finding = NetworkFinding(ConnectivityStatus.Unknown)!;

        Assert.Contains("No se pudo comprobar", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ElPortalCautivoDiceQueHayQueAbrirElNavegador()
    {
        Finding finding = NetworkFinding(ConnectivityStatus.CaptivePortal)!;

        Assert.Contains("navegador", finding.Title + finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ElLogRegistraElEstadoDeInternet()
    {
        // Sin esta línea no se puede saber, leyendo un log de un equipo del cliente, si había red.
        Assert.Contains(
            "internet              = DnsFailed",
            new SystemSnapshot { Network = ConnectivityStatus.DnsFailed }.ToLogSummary(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ElLogDistingueNoComprobadoDeSinRed() =>
        Assert.Contains(
            "internet              = no comprobado",
            new SystemSnapshot().ToLogSummary(),
            StringComparison.Ordinal);
}
