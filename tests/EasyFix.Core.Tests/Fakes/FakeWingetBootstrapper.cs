using EasyFix.Core.Apps;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>
/// Double de <see cref="IWingetBootstrapper"/>. Por defecto revienta el test si alguien lo invoca:
/// en los tests donde winget YA está disponible, el bootstrap nunca debería dispararse, y que
/// revienta en vez de devolver cualquier cosa es lo que haría visible esa regresión.
/// </summary>
public sealed class FakeWingetBootstrapper : IWingetBootstrapper
{
    private readonly Func<WingetBootstrapResult>? _respond;
    public int Calls { get; private set; }

    public FakeWingetBootstrapper(Func<WingetBootstrapResult>? respond = null) => _respond = respond;

    public static FakeWingetBootstrapper Forbidden() => new(() =>
        throw new InvalidOperationException("No se debería haber intentado el bootstrap de winget."));

    public static FakeWingetBootstrapper Succeeds(string detail = "winget instalado.") =>
        new(() => new WingetBootstrapResult(true, detail));

    public static FakeWingetBootstrapper Fails(string detail) =>
        new(() => new WingetBootstrapResult(false, detail));

    public Task<WingetBootstrapResult> EnsureAvailableAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(_respond?.Invoke() ?? new WingetBootstrapResult(true, "ok"));
    }
}
