using System.Net.Http;
using System.Net.Sockets;
using EasyFix.Core.Network;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Clasificación de fallos de red.
/// </summary>
/// <remarks>
/// Todo esto existe por el log del 2026-08-29: la descarga de Chrome falló con
/// <c>SocketException 11001</c> —el equipo no tenía internet— y EasyFix lo reportó como un error
/// genérico del paquete, para después culpar tres veces al catálogo de winget. La evidencia estaba;
/// faltaba traducirla.
/// </remarks>
public sealed class NetworkDiagnosisTests
{
    /// <summary>El caso exacto del log: HttpRequestException con el socket una capa más abajo.</summary>
    private static HttpRequestException ChromeCase() =>
        new("Host desconocido. (dl.google.com:443)",
            new SocketException((int)SocketError.HostNotFound));

    [Fact]
    public void EncuentraElSocketDentroDeLaExcepcionDeHttp()
    {
        // Mirar solo la excepción de afuera no encuentra nunca el código, que es el único dato que
        // distingue un DNS caído de un firewall.
        SocketException? found = NetworkDiagnosis.FindSocketException(ChromeCase());

        Assert.NotNull(found);
        Assert.Equal(SocketError.HostNotFound, found.SocketErrorCode);
    }

    [Fact]
    public void SinSocketEnLaCadena_DevuelveNull() =>
        Assert.Null(NetworkDiagnosis.FindSocketException(
            new InvalidOperationException("nada que ver con la red")));

    [Fact]
    public void ElCasoDelLog_SeClasificaComoDnsCaido()
    {
        ConnectivityResult result = NetworkDiagnosis.Classify(ChromeCase());

        Assert.Equal(ConnectivityStatus.DnsFailed, result.Status);
        Assert.False(result.IsOnline);
    }

    [Fact]
    public void ElDetalleTecnicoIncluyeElCodigoYLaCadenaCompleta()
    {
        // Sin las internas, el log dice «HttpRequestException: Host desconocido» y se pierde el
        // número. Con el número, el diagnóstico es inmediato.
        ConnectivityResult result = NetworkDiagnosis.Classify(ChromeCase());

        Assert.NotNull(result.TechnicalDetail);
        Assert.Contains("HttpRequestException", result.TechnicalDetail);
        Assert.Contains("SocketException", result.TechnicalDetail);
        Assert.Contains("HostNotFound", result.TechnicalDetail);
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NoData)]
    public void LosTresCodigosDeDns_DanDnsFailed(SocketError error) =>
        Assert.Equal(ConnectivityStatus.DnsFailed, NetworkDiagnosis.FromSocketError(error).Status);

    [Theory]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.ConnectionReset)]
    public void LosCodigosDeRutaYDeCorte_DanUnreachable(SocketError error) =>
        Assert.Equal(ConnectivityStatus.Unreachable, NetworkDiagnosis.FromSocketError(error).Status);

    [Fact]
    public void UnCodigoDesconocido_NoSeReportaComoOnline()
    {
        // Fallar cerrado: un código que no reconocemos igual significa que la conexión no funcionó.
        ConnectivityResult result = NetworkDiagnosis.FromSocketError(SocketError.ProtocolNotSupported);

        Assert.False(result.IsOnline);
        Assert.Equal(ConnectivityStatus.Unreachable, result.Status);
    }

    [Fact]
    public void UnTimeoutSinSocket_EsUnreachable()
    {
        // HttpClient corta por timeout con TaskCanceledException y sin socket adentro.
        ConnectivityResult result = NetworkDiagnosis.Classify(new TaskCanceledException("timeout"));

        Assert.Equal(ConnectivityStatus.Unreachable, result.Status);
    }

    [Fact]
    public void UnaExcepcionQueNoEsDeRed_QuedaEnUnknownYNoEnOnline()
    {
        ConnectivityResult result = NetworkDiagnosis.Classify(
            new InvalidOperationException("otra cosa"));

        Assert.Equal(ConnectivityStatus.Unknown, result.Status);
        Assert.False(result.IsOnline);
    }

    [Fact]
    public void UnknownNoCuentaComoConexion()
    {
        // Misma regla que el resto del diagnóstico: un dato que no se pudo medir nunca es verde. Una
        // comprobación fallida no es evidencia de que haya internet.
        var result = new ConnectivityResult(ConnectivityStatus.Unknown, "no se pudo");

        Assert.False(result.IsOnline);
    }

    [Fact]
    public void SoloOnlineCuentaComoConexion()
    {
        Assert.True(ConnectivityResult.Online().IsOnline);

        foreach (ConnectivityStatus status in Enum.GetValues<ConnectivityStatus>())
        {
            bool expected = status == ConnectivityStatus.Online;
            Assert.Equal(expected, new ConnectivityResult(status, "x").IsOnline);
        }
    }
}
