using System.Net.NetworkInformation;
using EasyFix.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Network;

/// <summary>Comprueba si el equipo tiene internet. Se abstrae para poder testear sin red.</summary>
public interface IConnectivityCheck
{
    /// <summary>
    /// Determina el estado de la conexión. Nunca tira excepción: un fallo se devuelve como estado.
    /// </summary>
    Task<ConnectivityResult> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// Comprueba la conexión en dos etapas: primero el adaptador, después una petición real.
/// </summary>
/// <remarks>
/// <para><b>Por qué dos etapas.</b> <see cref="NetworkInterface.GetIsNetworkAvailable"/> es
/// instantáneo y no genera tráfico, pero solo dice si hay una interfaz levantada: un equipo enchufado
/// a un router sin internet da <c>true</c>. Es un descarte rápido del caso obvio —cable
/// desenchufado—, no una respuesta. La única forma de saber si hay internet es pedir algo y ver qué
/// vuelve.</para>
///
/// <para><b>Por qué la comprobación va por HTTP y no HTTPS.</b> Es deliberado y es lo que hace que se
/// pueda detectar un portal cautivo. Un wifi de hotel o de aeropuerto intercepta la petición y
/// contesta su propia página de login; sobre HTTP eso se ve como «llegó una respuesta con el cuerpo
/// equivocado», que es exactamente el diagnóstico correcto. Sobre HTTPS el portal produce un error de
/// certificado indistinguible de un firewall, y el técnico termina buscando el problema donde no
/// está.</para>
///
/// <para><b>No es un agujero de seguridad.</b> Acá no se descarga nada que se vaya a ejecutar: se
/// piden 22 bytes de texto y se comparan contra una constante. Los instaladores siguen exigiendo
/// HTTPS en <c>HttpFileDownloader</c>, sin excepción.</para>
///
/// <para>El endpoint por defecto es el mismo que usa Windows para el ícono de red (NCSI). Es
/// configurable en <c>appsettings.json</c> por si una red corporativa lo bloquea.</para>
/// </remarks>
public sealed class HttpConnectivityCheck : IConnectivityCheck, IDisposable
{
    private readonly HttpClient _http;
    private readonly NetworkOptions _options;
    private readonly ILogger<HttpConnectivityCheck> _logger;

    public HttpConnectivityCheck(NetworkOptions options, ILogger<HttpConnectivityCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 60)),
        };

        // Sin esto, un proxy o el propio Windows pueden servir una respuesta cacheada y la
        // comprobación diría «hay internet» sobre una red que se cayó hace media hora.
        _http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EasyFix/1.0");
    }

    public async Task<ConnectivityResult> CheckAsync(CancellationToken ct = default)
    {
        if (!TryGetIsNetworkAvailable())
        {
            _logger.LogWarning("Ninguna interfaz de red está conectada.");

            return new ConnectivityResult(
                ConnectivityStatus.NoAdapter,
                "El equipo no está conectado a ninguna red. Revisá el cable de red o el wifi.",
                "NetworkInterface.GetIsNetworkAvailable() == false");
        }

        if (!Uri.TryCreate(_options.ProbeUrl, UriKind.Absolute, out Uri? probe))
        {
            _logger.LogError("La URL de comprobación '{Url}' no es válida.", _options.ProbeUrl);

            return new ConnectivityResult(
                ConnectivityStatus.Unknown,
                "No se pudo comprobar si el equipo tiene internet.",
                $"Network.ProbeUrl inválida: '{_options.ProbeUrl}'");
        }

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(probe, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Un 302 hacia otro sitio, o un 403: la respuesta no es del servidor que pedimos.
                return new ConnectivityResult(
                    ConnectivityStatus.CaptivePortal,
                    "El equipo está conectado a una red que exige iniciar sesión en el navegador " +
                    "antes de dar internet (wifi de hotel, aeropuerto o cafetería). " +
                    "Abrí el navegador, aceptá esa página y volvé a intentar.",
                    $"{probe} respondió {(int)response.StatusCode} {response.StatusCode}");
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!body.Contains(_options.ExpectedBody, StringComparison.Ordinal))
            {
                return new ConnectivityResult(
                    ConnectivityStatus.CaptivePortal,
                    "El equipo está conectado a una red que exige iniciar sesión en el navegador " +
                    "antes de dar internet (wifi de hotel, aeropuerto o cafetería). " +
                    "Abrí el navegador, aceptá esa página y volvé a intentar.",
                    $"{probe} contestó {body.Length} caracteres que no contienen " +
                    $"'{_options.ExpectedBody}'");
            }

            _logger.LogInformation("Conexión a internet confirmada contra {Url}.", probe);
            return ConnectivityResult.Online();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El usuario canceló: no es un problema de red y no se puede reportar como tal.
            throw;
        }
        catch (Exception ex)
        {
            ConnectivityResult result = NetworkDiagnosis.Classify(ex);

            _logger.LogWarning(
                "Sin internet ({Status}) contra {Url}. {Technical}",
                result.Status, probe, result.TechnicalDetail);

            return result;
        }
    }

    /// <summary>
    /// <c>true</c> si hay alguna interfaz de red levantada.
    /// </summary>
    /// <remarks>
    /// Ante un fallo de la consulta se devuelve <c>true</c> para seguir a la etapa 2: es la petición
    /// real la que decide. Fallar acá y declarar «sin red» convertiría un problema de esta función en
    /// un diagnóstico equivocado, que es justamente lo que se está corrigiendo.
    /// </remarks>
    private bool TryGetIsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException ex)
        {
            _logger.LogWarning(ex, "No se pudo consultar el estado de las interfaces de red.");
            return true;
        }
    }

    public void Dispose() => _http.Dispose();
}
