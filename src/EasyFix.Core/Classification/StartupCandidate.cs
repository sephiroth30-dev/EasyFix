namespace EasyFix.Core.Classification;

/// <summary>Dónde se encontró la entrada de autoarranque.</summary>
public enum StartupLocation
{
    RegistryRunCurrentUser,
    RegistryRunLocalMachine,
    RegistryRunWow6432Node,
    StartupFolderCurrentUser,
    StartupFolderAllUsers,
    ScheduledTaskAtLogon,
    Service,
}

/// <summary>
/// Resultado de verificar la firma Authenticode de un binario.
/// </summary>
/// <param name="IsValid">
/// La cadena de confianza valida. Si es <c>false</c> (sin firmar, firma alterada, cert vencido o
/// no confiable), la entrada NUNCA puede llegar a automático.
/// </param>
/// <param name="PublisherOrganization">
/// El campo <c>O=</c> del subject del certificado — p. ej. <c>"Spotify AB"</c>. Este es el dato de
/// identidad: se puede verificar criptográficamente, a diferencia de un nombre de archivo.
/// </param>
public sealed record SignatureInfo(bool IsValid, string? PublisherOrganization)
{
    public static SignatureInfo Unsigned { get; } = new(false, null);
}

/// <summary>
/// Todo lo que el clasificador necesita saber de una entrada de autoarranque. Es un DTO puro: quien
/// lo construye (registro, autorunsc, WMI) es otro problema, y así el clasificador se testea sin
/// tocar Windows.
/// </summary>
/// <param name="Id">Identidad estable: ruta de clave de registro, ruta de archivo o nombre de servicio.</param>
/// <param name="DisplayName">Nombre visible al usuario.</param>
/// <param name="ExecutablePath">Ruta canonicalizada del ejecutable, si se pudo resolver.</param>
/// <param name="ProductName">Nombre de producto de los metadatos del binario, si existe.</param>
/// <param name="Signature">Firma Authenticode.</param>
/// <param name="Location">Dónde estaba.</param>
/// <param name="ServiceName">Nombre corto del servicio, cuando <see cref="Location"/> es Service.</param>
/// <param name="RunningDependents">Servicios EN EJECUCIÓN que dependen de este. Si hay alguno, no se toca.</param>
/// <param name="HasAssociatedDriver">Hay un driver de sistema apuntando al mismo ejecutable (audio, GPU, chipset, red).</param>
/// <param name="IsRegisteredSecurityProduct">Está registrado en <c>root\SecurityCenter2</c>.</param>
public sealed record StartupCandidate(
    string Id,
    string DisplayName,
    string? ExecutablePath,
    string? ProductName,
    SignatureInfo Signature,
    StartupLocation Location,
    string? ServiceName = null,
    IReadOnlyList<string>? RunningDependents = null,
    bool HasAssociatedDriver = false,
    bool IsRegisteredSecurityProduct = false)
{
    public IReadOnlyList<string> Dependents => RunningDependents ?? Array.Empty<string>();
}

/// <summary>
/// Estado del equipo que cambia las decisiones del clasificador.
/// </summary>
/// <param name="IsDomainJoined">
/// En equipo de dominio las GPO revierten los cambios (el fix parece funcionar y se deshace solo) y
/// desactivar agentes corporativos rompe el equipo para el área de TI.
/// </param>
/// <param name="IsSsd">Condiciona la oferta de SysMain: en HDD, Superfetch AYUDA.</param>
/// <param name="TotalRamGb">Idem.</param>
public sealed record SystemContext(
    bool IsDomainJoined,
    bool IsSsd,
    double TotalRamGb);
