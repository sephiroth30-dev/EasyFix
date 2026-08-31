using System.Net.Sockets;

namespace EasyFix.Core.Network;

/// <summary>
/// En qué estado está la conexión del equipo.
/// </summary>
/// <remarks>
/// Los cuatro estados de fallo son distintos <b>a propósito</b>: cada uno lleva a una acción distinta
/// del técnico. «Sin conexión» a secas manda a revisar el cable cuando el problema puede ser el
/// portal cautivo de un wifi de hotel.
/// </remarks>
public enum ConnectivityStatus
{
    /// <summary>Hay internet de verdad: se resolvió el nombre y contestó lo que se esperaba.</summary>
    Online,

    /// <summary>Ninguna interfaz de red está conectada. Cable desenchufado o wifi apagado.</summary>
    NoAdapter,

    /// <summary>Hay red pero no resuelve nombres. DNS mal configurado, o el router no responde.</summary>
    DnsFailed,

    /// <summary>Resuelve el nombre pero no llega. Firewall, proxy, o sin ruta a internet.</summary>
    Unreachable,

    /// <summary>
    /// Contesta, pero contesta otra cosa: el wifi está detrás de un portal cautivo que hay que
    /// aceptar en el navegador antes de tener internet.
    /// </summary>
    CaptivePortal,

    /// <summary>No se pudo determinar. Se trata como «no hay», nunca como «hay».</summary>
    Unknown,
}

/// <param name="Status">Qué se determinó.</param>
/// <param name="Detail">Explicación en castellano, lista para mostrar.</param>
/// <param name="TechnicalDetail">Lo que hay que leer en el log: excepción, código, host.</param>
public sealed record ConnectivityResult(
    ConnectivityStatus Status,
    string Detail,
    string? TechnicalDetail = null)
{
    /// <summary>
    /// <c>true</c> solo cuando hay internet confirmado.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectivityStatus.Unknown"/> devuelve <c>false</c>: una comprobación que no se pudo
    /// hacer no es evidencia de que haya conexión. Es la misma regla que el resto del diagnóstico —un
    /// dato ausente nunca se cuenta como verde.
    /// </remarks>
    public bool IsOnline => Status == ConnectivityStatus.Online;

    public static ConnectivityResult Online() =>
        new(ConnectivityStatus.Online, "El equipo tiene conexión a internet.");
}

/// <summary>
/// Traduce una excepción de red al estado que representa. Lógica pura: se testea sin tocar la red.
/// </summary>
/// <remarks>
/// <para><b>De dónde salió.</b> En el log del 2026-08-29 la descarga de Chrome falló con
/// <c>SocketException (11001)</c> resolviendo <c>dl.google.com</c>. Ese código es
/// <c>WSAHOST_NOT_FOUND</c> y significa que el equipo no tiene internet, pero EasyFix lo mostró como
/// un mensaje de excepción en crudo y después culpó tres veces al catálogo de winget de un problema
/// que no era suyo. Esta clase es lo que faltaba para no volver a equivocar el diagnóstico.</para>
///
/// <para>Los números son de Winsock, no de .NET: <see cref="SocketException.SocketErrorCode"/> los
/// expone como enum, que es más legible y no depende de la plataforma.</para>
/// </remarks>
public static class NetworkDiagnosis
{
    /// <summary>
    /// Busca una excepción de socket en toda la cadena de excepciones internas.
    /// </summary>
    /// <remarks>
    /// <c>HttpClient</c> envuelve el <see cref="SocketException"/> dentro de un
    /// <c>HttpRequestException</c>, así que mirar solo la de afuera no encuentra nunca el código real.
    /// Exactamente el caso del log: la línea visible decía <c>HttpRequestException</c> y el motivo
    /// estaba una capa más abajo.
    /// </remarks>
    public static SocketException? FindSocketException(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket;
            }
        }

        return null;
    }

    /// <summary>Clasifica una excepción ocurrida al intentar usar la red.</summary>
    public static ConnectivityResult Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string technical = Describe(exception);

        if (FindSocketException(exception) is { } socket)
        {
            return FromSocketError(socket.SocketErrorCode, technical);
        }

        // Un timeout de HttpClient llega como TaskCanceledException sin socket adentro. Que el
        // servidor no conteste a tiempo es indistinguible, desde acá, de no tener ruta hacia él.
        if (exception is TaskCanceledException or TimeoutException)
        {
            return new ConnectivityResult(
                ConnectivityStatus.Unreachable,
                "El equipo no recibió respuesta de internet dentro del tiempo de espera. " +
                "Suele ser un firewall, un proxy o una conexión muy lenta.",
                technical);
        }

        return new ConnectivityResult(
            ConnectivityStatus.Unknown,
            "No se pudo comprobar si el equipo tiene internet.",
            technical);
    }

    /// <summary>Clasifica un código de Winsock.</summary>
    public static ConnectivityResult FromSocketError(SocketError error, string? technical = null) =>
        error switch
        {
            // 11001, 11002, 11004. El nombre no resuelve: no hay DNS que funcione.
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData =>
                new ConnectivityResult(
                    ConnectivityStatus.DnsFailed,
                    "El equipo no puede resolver nombres de internet. Está conectado a la red local " +
                    "pero no llega a internet, o el DNS está mal configurado.",
                    technical),

            // 10051, 10065. El sistema sabe que no hay por dónde salir.
            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                new ConnectivityResult(
                    ConnectivityStatus.Unreachable,
                    "El equipo no tiene ruta hacia internet. Revisá el cable, el wifi y el router.",
                    technical),

            // 10060, 10061, 10013, 10054. Se intentó y algo lo cortó.
            SocketError.TimedOut or SocketError.ConnectionRefused
                or SocketError.AccessDenied or SocketError.ConnectionReset =>
                new ConnectivityResult(
                    ConnectivityStatus.Unreachable,
                    "El equipo llega a la red pero la conexión se rechazó o se cortó. " +
                    "Suele ser un firewall, un antivirus o un proxy.",
                    technical),

            _ => new ConnectivityResult(
                ConnectivityStatus.Unreachable,
                "Falló la conexión a internet.",
                technical ?? $"SocketError.{error}"),
        };

    /// <summary>
    /// La cadena de excepciones en una línea, para el log.
    /// </summary>
    /// <remarks>
    /// Sin las internas, el log dice «HttpRequestException: Host desconocido» y se pierde el número,
    /// que es el único dato que distingue un DNS caído de un firewall.
    /// </remarks>
    private static string Describe(Exception exception)
    {
        var parts = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string part = $"{current.GetType().Name}: {current.Message}";

            if (current is SocketException socket)
            {
                part += $" (SocketError.{socket.SocketErrorCode}, {socket.ErrorCode})";
            }

            parts.Add(part);
        }

        return string.Join(" -> ", parts);
    }
}
