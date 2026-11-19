namespace EasyFix.Core.Drivers;

/// <summary>Resultado de intentar instalar un grupo de actualizaciones de driver.</summary>
/// <param name="Success"><c>true</c> solo si el instalador de Windows Update confirmó éxito.</param>
/// <param name="RebootRequired">Si hace falta reiniciar para que el driver quede activo.</param>
/// <param name="Detail">Qué pasó, en español, para el reporte y el log.</param>
public sealed record DriverInstallResult(bool Success, bool RebootRequired, string Detail);

/// <summary>
/// Resultado de buscar actualizaciones de driver.
/// </summary>
/// <param name="Updates">Lo que se encontró. Vacía y <see cref="Succeeded"/> en <c>true</c> es un
/// hecho medido: «no hay actualizaciones». Vacía con <see cref="Succeeded"/> en <c>false</c> es
/// justo lo contrario: «no se pudo averiguar». Las dos NUNCA deben mostrarse igual — es la misma
/// regla que ya aplica en todo el resto del diagnóstico.</param>
/// <param name="Succeeded">Si la consulta al catálogo se completó.</param>
/// <param name="FailureReason">Por qué no se pudo, cuando <see cref="Succeeded"/> es <c>false</c>.</param>
public sealed record DriverSearchResult(
    IReadOnlyList<DriverUpdate> Updates,
    bool Succeeded,
    string? FailureReason = null)
{
    public static DriverSearchResult Ok(IReadOnlyList<DriverUpdate> updates) => new(updates, true);

    public static DriverSearchResult Failed(string reason) =>
        new(Array.Empty<DriverUpdate>(), false, reason);
}

/// <summary>Puerto hacia el catálogo de drivers. Existe para poder testear sin Windows Update real.</summary>
public interface IDriverCatalog
{
    /// <summary>Actualizaciones de driver disponibles para este equipo, ya clasificadas. Solo lectura.</summary>
    Task<DriverSearchResult> SearchAsync(CancellationToken ct);

    /// <summary>
    /// Descarga e instala los updates indicados por <see cref="DriverUpdate.UpdateId"/>. El
    /// llamador es responsable de filtrar antes a <see cref="DriverRiskClass.Safe"/> — este método
    /// instala lo que se le pida, sin volver a clasificar.
    /// </summary>
    Task<DriverInstallResult> InstallAsync(
        IReadOnlyList<string> updateIds, IProgress<string>? progress, CancellationToken ct);
}
