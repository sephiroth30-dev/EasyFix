using System.Text.Json.Serialization;

namespace EasyFix.Core.Configuration;

/// <summary>
/// Modelo de <c>appsettings.json</c>. Umbrales y listas curadas viven en configuración, nunca como
/// constantes en C#: se ajustan sin recompilar y sin volver a firmar el .exe.
/// </summary>
public sealed record EasyFixOptions
{
    public BrandingOptions Branding { get; init; } = new();
    public ThresholdOptions Thresholds { get; init; } = new();
    public ClassifierOptions Classifier { get; init; } = new();
    public BloatwareOptions Bloatware { get; init; } = new();
    public ServiceOptions Services { get; init; } = new();
    public NetworkOptions Network { get; init; } = new();
    public IReadOnlyList<WingetPackage> WingetPackages { get; init; } = Array.Empty<WingetPackage>();
}

/// <summary>
/// Cómo se comprueba que el equipo tiene internet.
/// </summary>
/// <remarks>
/// Es configurable porque el endpoint por defecto —el mismo que usa Windows para decidir si el ícono
/// de red lleva el signo de exclamación— puede estar bloqueado en una red corporativa. Sin poder
/// cambiarlo, EasyFix reportaría «sin internet» en un equipo que sí lo tiene.
/// </remarks>
public sealed record NetworkOptions
{
    /// <summary>
    /// URL de la comprobación. <b>HTTP a propósito</b>: ver <c>HttpConnectivityCheck</c>. Sobre HTTPS
    /// un portal cautivo se vuelve indistinguible de un firewall.
    /// </summary>
    public string ProbeUrl { get; init; } = "http://www.msftconnecttest.com/connecttest.txt";

    /// <summary>
    /// Texto que tiene que aparecer en la respuesta. Si llega otra cosa, hay un portal cautivo en el
    /// medio: la petición «funcionó» pero no habla con quien creemos.
    /// </summary>
    public string ExpectedBody { get; init; } = "Microsoft Connect Test";

    /// <summary>
    /// Tope de la comprobación. Corto a propósito: es un chequeo previo, no puede hacer esperar al
    /// técnico frente a una pantalla quieta.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 8;
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

    /// <summary>
    /// Quitar el límite de un punto de restauración por día antes de crear el propio.
    /// </summary>
    /// <remarks>
    /// Windows no crea más de un punto cada 24 h salvo que se ponga
    /// <c>SystemRestorePointCreationFrequency</c> en 0. En el equipo de un cliente que ya tuvo
    /// actividad ese día, ese límite impide que la herramienta cree su red de seguridad — que es
    /// exactamente lo que pasó en la primera prueba real. Se cambia informando el valor anterior, se
    /// registra en el journal y «Deshacer todo» lo revierte.
    /// <para>El costo es más espacio en copias de sombra, acotado por el límite que ya tiene
    /// configurado Restaurar sistema.</para>
    /// </remarks>
    public bool DisableRestorePointThrottle { get; init; } = true;

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

    /// <summary>
    /// Descarga desde el origen oficial, para lo que no está en winget.
    /// </summary>
    /// <remarks>
    /// Cuando está presente se usa esta vía y no winget. Hace falta porque el catálogo de winget
    /// cambia: RustDesk fue removido en 2026 por un falso positivo de antivirus, y su ID dejó de
    /// resolver de un día para el otro.
    /// </remarks>
    public DirectDownload? Direct { get; init; }
}

/// <param name="Url">URL fija del instalador. Se ignora si hay <see cref="GitHubRepository"/>.</param>
/// <param name="GitHubRepository">
/// <c>owner/repo</c>. Se consulta la API de releases y se toma el más reciente, así que no queda
/// ninguna versión clavada que envejezca.
/// </param>
/// <param name="AssetPattern">Fragmento que debe contener el nombre del asset: <c>x86_64.exe</c>.</param>
/// <param name="SilentArgs">Argumentos para instalar sin interacción.</param>
public sealed record DirectDownload
{
    public string? Url { get; init; }

    [JsonPropertyName("githubRepository")]
    public string? GitHubRepository { get; init; }

    public string? AssetPattern { get; init; }

    public IReadOnlyList<string>? SilentArgs { get; init; }

    /// <summary>
    /// Patrón alternativo, que se intenta primero. Sirve para preferir un MSI sobre un EXE.
    /// </summary>
    /// <remarks>
    /// El instalador EXE de RustDesk con <c>--silent-install</c> se quedó colgado 40 minutos en un
    /// equipo real. Su propia documentación recomienda el MSI, que con <c>/qn</c> es determinista.
    /// </remarks>
    public string? PreferredAssetPattern { get; init; }

    /// <summary>
    /// Minutos que se le dan al instalador.
    /// </summary>
    /// <remarks>
    /// Por defecto 8, no los 30 del timeout general: ese está pensado para DISM. Un instalador que
    /// tarda más de ocho minutos está colgado, y esperarlo bloquea toda la tanda — pasó exactamente
    /// eso con RustDesk.
    /// </remarks>
    public int TimeoutMinutes { get; init; } = 8;

    /// <summary><c>true</c> si el asset a instalar es un MSI y hay que lanzarlo con msiexec.</summary>
    public static bool IsMsi(string path) =>
        Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Marca del técnico. Aparece en la ventana, en el informe y en el journal.</summary>
public sealed record BrandingOptions
{
    public string ProductName { get; init; } = "EasyFix";

    /// <summary>Nombre del técnico. Vacío = no se muestra ninguna atribución.</summary>
    public string TechnicianName { get; init; } = string.Empty;

    /// <summary>Contacto opcional para el pie del informe que se le deja al cliente.</summary>
    public string? Contact { get; init; }

    /// <summary>"EasyFix — por Andrés Hernández", o solo "EasyFix" si no hay nombre.</summary>
    public string WindowTitle => string.IsNullOrWhiteSpace(TechnicianName)
        ? ProductName
        : $"{ProductName} — por {TechnicianName}";

    public string? Attribution => string.IsNullOrWhiteSpace(TechnicianName)
        ? null
        : $"por {TechnicianName}";
}
