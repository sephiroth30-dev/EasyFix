namespace EasyFix.Core.Abstractions;

/// <summary>Resultado de un proceso externo. Nunca se asume éxito por "terminó".</summary>
/// <param name="ExitCode">Código de salida. <c>null</c> si se cortó por timeout.</param>
/// <param name="StandardOutput">stdout completo.</param>
/// <param name="StandardError">stderr completo.</param>
/// <param name="TimedOut">true si se mató por exceder el timeout.</param>
/// <param name="Duration">Cuánto tardó.</param>
public sealed record ProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Ejecuta binarios de Windows (<c>powercfg</c>, <c>DISM</c>, <c>fsutil</c>, <c>winget</c>,
/// <c>robocopy</c>, <c>chkdsk</c>, <c>netsh</c>).
/// </summary>
/// <remarks>
/// Los argumentos llegan como <b>lista</b>, no como string. Es intencional: los valores vienen del
/// registro, de rutas de perfil y de <c>appsettings.json</c>, y una concatenación de strings abre
/// argument injection y bugs de quoting con rutas que tienen espacios.
/// </remarks>
public interface IProcessRunner
{
    /// <param name="executablePath">
    /// Ruta ABSOLUTA al ejecutable. Nunca un nombre suelto a resolver por <c>PATH</c>: eso permite
    /// que un binario homónimo en el directorio actual secuestre la llamada.
    /// </param>
    /// <param name="arguments">Un elemento por argumento. El escaping lo hace el runtime.</param>
    Task<ProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default);
}
