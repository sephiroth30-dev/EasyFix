using System.Text.Json.Serialization;

namespace EasyFix.Core.Configuration;

/// <summary>
/// Modelo de <c>appsettings.json</c>. Umbrales y listas curadas viven en configuración, nunca como
/// constantes en C#: se ajustan sin recompilar y sin volver a firmar el .exe.
/// </summary>
public sealed record EasyFixOptions
{
    public ThresholdOptions Thresholds { get; init; } = new();
    public ClassifierOptions Classifier { get; init; } = new();
    public BloatwareOptions Bloatware { get; init; } = new();
    public ServiceOptions Services { get; init; } = new();
    public IReadOnlyList<WingetPackage> WingetPackages { get; init; } = Array.Empty<WingetPackage>();
}

public sealed record ThresholdOptions
{
    public int LowDiskFreePercent { get; init; } = 15;
    public int MinRamGb { get; init; } = 8;
    public int TempFileMinAgeMinutes { get; init; } = 60;
    public int WindowsOldMinAgeDays { get; init; } = 10;
    public int DiskLatencyWarnMs { get; init; } = 25;
    public int CommitPressurePercent { get; init; } = 85;
    public int BatteryWearPercent { get; init; } = 30;
    public int CpuThrottleFrequencyPercent { get; init; } = 70;
    public int QuickScanBudgetSeconds { get; init; } = 10;
    public int PerCheckTimeoutSeconds { get; init; } = 5;
    public int MaxConcurrentChecks { get; init; } = 8;
    public int ExternalProcessTimeoutMinutes { get; init; } = 30;

    public TimeSpan TempFileMinAge => TimeSpan.FromMinutes(TempFileMinAgeMinutes);
    public TimeSpan PerCheckTimeout => TimeSpan.FromSeconds(PerCheckTimeoutSeconds);
    public TimeSpan ExternalProcessTimeout => TimeSpan.FromMinutes(ExternalProcessTimeoutMinutes);
}

public sealed record ClassifierOptions
{
    [JsonPropertyName("Layer1_HardBlock")]
    public HardBlockOptions HardBlock { get; init; } = new();

    [JsonPropertyName("Layer2_AutoDisableAllowlist")]
    public IReadOnlyList<AllowlistEntry> AutoDisableAllowlist { get; init; } = Array.Empty<AllowlistEntry>();
}

public sealed record HardBlockOptions
{
    public IReadOnlyList<string> SecurityCenterClasses { get; init; } = Array.Empty<string>();

    /// <summary>Prefijos con variables de entorno ya expandidas por <see cref="OptionsLoader"/>.</summary>
    public IReadOnlyList<string> ProtectedPathPrefixes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Organizaciones (<c>O=</c>) de certificados intocables: Microsoft, fabricantes de VPN/EDR/
    /// backup y de drivers. Por <b>certificado</b>, no por nombre de archivo.
    /// </summary>
    public IReadOnlyList<string> ProtectedCertificatePublishers { get; init; } = Array.Empty<string>();

    public bool BlockIfHasRunningDependents { get; init; } = true;
    public bool BlockIfDriverAssociated { get; init; } = true;
}

/// <remarks>
/// Propiedades <c>init</c> en vez de record posicional: el binding por constructor de
/// System.Text.Json depende de que los nombres de parámetro casen con las claves JSON, y es más
/// frágil que el binding por propiedad. Las claves del archivo van en minúscula
/// (<c>publisher</c>, <c>product</c>) y funcionan porque el loader usa
/// <c>PropertyNameCaseInsensitive</c>.
/// </remarks>
public sealed record AllowlistEntry
{
    /// <summary>Organización (<c>O=</c>) del certificado Authenticode. Comparación exacta.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>Producto de ese publisher. Ver la nota de diseño en <see cref="Classification.StartupClassifier"/>.</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Qué pierde el usuario, en español.</summary>
    public string? Loses { get; init; }

    /// <summary>Para casos como Teams: la versión personal es prescindible, la corporativa no.</summary>
    public bool OnlyIfNotDomainJoined { get; init; }
}

public sealed record BloatwareOptions
{
    public IReadOnlyList<BloatwareCandidate> Candidates { get; init; } = Array.Empty<BloatwareCandidate>();
}

public sealed record BloatwareCandidate
{
    public string Publisher { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

public sealed record ServiceOptions
{
    public IReadOnlyList<ServiceCandidate> OfferToDisable { get; init; } = Array.Empty<ServiceCandidate>();
}

public sealed record ServiceCandidate
{
    public string Name { get; init; } = string.Empty;

    public string Loses { get; init; } = string.Empty;

    /// <summary>
    /// Condición para siquiera OFRECER el servicio. Valores reconocidos:
    /// <c>NoPrintersInstalled</c>, <c>NoBluetoothAdapter</c>, <c>SsdAndRamAtLeast8Gb</c>.
    /// Una condición desconocida hace que el servicio NO se ofrezca — fallar cerrado, no abierto.
    /// </summary>
    public string? OnlyIf { get; init; }
}

public sealed record WingetPackage
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("default")]
    public bool IsDefault { get; init; }

    public string? Note { get; init; }
}
