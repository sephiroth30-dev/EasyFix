namespace EasyFix.Core.Diagnostics;

public enum Severity
{
    Info,
    Warning,

    /// <summary>Requiere atención antes de seguir. Puede bloquear los fixes (SMART fallando).</summary>
    Critical,
}

/// <summary>Un número medido, ya formateado para el reporte.</summary>
/// <param name="Label">Etiqueta en español: "Tiempo de arranque".</param>
/// <param name="Value">Valor formateado: "94,0".</param>
/// <param name="Unit">Unidad: "s", "GB", "%". <c>null</c> si no aplica.</param>
public sealed record Metric(string Label, string Value, string? Unit = null)
{
    public override string ToString() =>
        Unit is null ? $"{Label}: {Value}" : $"{Label}: {Value} {Unit}";
}

/// <summary>Un hallazgo del diagnóstico. Los chequeos devuelven <c>null</c> cuando todo está bien.</summary>
/// <param name="CheckId">Identificador estable, en kebab-case con punto: <c>"disk.media-type"</c>.</param>
/// <param name="Severity">Gravedad.</param>
/// <param name="Title">Título corto.</param>
/// <param name="Detail">Explicación en español, redactada para que el cliente la entienda.</param>
/// <param name="Metrics">Los números que respaldan el hallazgo. Sin esto es una opinión.</param>
/// <param name="SuggestedFixIds">Fixes que lo resuelven. Vacío si requiere hardware.</param>
public sealed record Finding(
    string CheckId,
    Severity Severity,
    string Title,
    string Detail,
    IReadOnlyList<Metric>? Metrics = null,
    IReadOnlyList<string>? SuggestedFixIds = null)
{
    public IReadOnlyList<Metric> AllMetrics => Metrics ?? Array.Empty<Metric>();

    public IReadOnlyList<string> Fixes => SuggestedFixIds ?? Array.Empty<string>();
}

/// <summary>
/// Un chequeo de diagnóstico. <b>Nunca modifica nada.</b>
/// </summary>
/// <remarks>
/// Que toda la fase de diagnóstico sea de solo lectura es lo que la hace segura de correr en el
/// equipo de un cliente sin haberla probado antes, y lo que permite entregar valor (decir qué hay que
/// hacer) antes de que exista un solo fix.
/// </remarks>
public interface IDiagnosticCheck
{
    /// <summary>Identificador estable. Se usa en el reporte y en los logs.</summary>
    string Id { get; }

    /// <summary>Nombre corto para mostrar mientras corre.</summary>
    string DisplayName { get; }

    /// <summary><c>true</c> si es un chequeo lento (DISM, chkdsk) y va detrás de "Reparar errores".</summary>
    bool IsDeepScan { get; }

    /// <summary>Devuelve <c>null</c> cuando no hay nada que reportar.</summary>
    Task<Finding?> RunAsync(CancellationToken ct);
}

/// <summary>Por qué un chequeo no dio resultado.</summary>
/// <param name="CheckId">Cuál.</param>
/// <param name="Reason">Qué pasó, para el log y el reporte.</param>
/// <param name="TimedOut"><c>true</c> si fue por timeout y no por excepción.</param>
public sealed record CheckFailure(string CheckId, string Reason, bool TimedOut);

/// <summary>Resultado de una corrida del diagnóstico.</summary>
/// <param name="Findings">Hallazgos, ordenados de más grave a menos.</param>
/// <param name="Failures">
/// Chequeos que no se pudieron completar. Se muestran como "no determinado", nunca como "todo bien":
/// un chequeo que falló no es evidencia de que el equipo esté sano.
/// </param>
/// <param name="Duration">Cuánto tardó el escaneo completo.</param>
public sealed record ScanReport(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<CheckFailure> Failures,
    TimeSpan Duration)
{
    public bool HasCritical => Findings.Any(f => f.Severity == Severity.Critical);

    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);

    /// <summary><c>true</c> si algún chequeo no se pudo completar: el reporte está incompleto.</summary>
    public bool IsPartial => Failures.Count > 0;
}
