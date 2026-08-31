using EasyFix.Core.Network;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>
/// Comprobación de conexión que devuelve lo que se le diga, sin tocar la red.
/// </summary>
/// <remarks>
/// Cuenta las llamadas: la compuerta de red tiene que evaluarse <b>una sola vez por tanda</b>, no una
/// vez por paquete. Diez paquetes con diez comprobaciones de 8 s serían 80 s de espera antes de
/// instalar nada.
/// </remarks>
public sealed class FakeConnectivityCheck : IConnectivityCheck
{
    private readonly ConnectivityResult _result;

    public FakeConnectivityCheck(ConnectivityResult? result = null) =>
        _result = result ?? ConnectivityResult.Online();

    /// <summary>Cuántas veces se preguntó por el estado de la red.</summary>
    public int Calls { get; private set; }

    public Task<ConnectivityResult> CheckAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_result);
    }

    public static FakeConnectivityCheck Online() => new();

    public static FakeConnectivityCheck Offline(
        ConnectivityStatus status = ConnectivityStatus.DnsFailed) =>
        new(new ConnectivityResult(status, "Sin internet (fake).", "fake"));
}
