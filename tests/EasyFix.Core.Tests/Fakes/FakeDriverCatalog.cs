using EasyFix.Core.Drivers;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>Double de <see cref="IDriverCatalog"/> — nunca toca COM ni Windows Update real.</summary>
public sealed class FakeDriverCatalog : IDriverCatalog
{
    private readonly DriverSearchResult _searchResult;
    private readonly Func<IReadOnlyList<string>, DriverInstallResult>? _install;

    public List<string> InstalledUpdateIds { get; } = new();

    public FakeDriverCatalog(
        DriverSearchResult searchResult,
        Func<IReadOnlyList<string>, DriverInstallResult>? install = null)
    {
        _searchResult = searchResult;
        _install = install;
    }

    public static FakeDriverCatalog Empty() =>
        new(DriverSearchResult.Ok(Array.Empty<DriverUpdate>()));

    public static FakeDriverCatalog WithUpdates(params DriverUpdate[] updates) =>
        new(DriverSearchResult.Ok(updates));

    public static FakeDriverCatalog SearchFails(string reason) =>
        new(DriverSearchResult.Failed(reason));

    public Task<DriverSearchResult> SearchAsync(CancellationToken ct) => Task.FromResult(_searchResult);

    public Task<DriverInstallResult> InstallAsync(
        IReadOnlyList<string> updateIds, IProgress<string>? progress, CancellationToken ct)
    {
        InstalledUpdateIds.AddRange(updateIds);

        DriverInstallResult result = _install?.Invoke(updateIds)
            ?? new DriverInstallResult(true, false, "Instalado (fake).");

        return Task.FromResult(result);
    }
}
