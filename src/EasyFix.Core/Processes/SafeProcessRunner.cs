using System.Diagnostics;
using System.Text;
using EasyFix.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Processes;

/// <summary>
/// Ejecuta binarios de Windows con las tres defensas puestas: argumentos como lista, sin shell, y
/// ruta absoluta al ejecutable.
/// </summary>
/// <remarks>
/// <para><b>Argumentos como lista.</b> Se usa <see cref="ProcessStartInfo.ArgumentList"/> y nunca
/// <c>ProcessStartInfo.Arguments</c>. Los valores vienen del registro, de rutas de perfil y de
/// <c>appsettings.json</c>; concatenar strings abre argument injection y rompe con cualquier ruta
/// que tenga espacios.</para>
///
/// <para><b>Sin shell.</b> <c>UseShellExecute = false</c>: nada pasa por <c>cmd.exe</c>, así que
/// <c>&amp;</c>, <c>|</c> y <c>&gt;</c> dentro de un argumento son texto, no operadores.</para>
///
/// <para><b>Ruta absoluta.</b> Resolver por <c>PATH</c> permitiría que un <c>fsutil.exe</c> puesto en
/// el directorio actual —en un equipo comprometido, que es justo el que estamos reparando— secuestre
/// la llamada.</para>
/// </remarks>
public sealed class SafeProcessRunner : IProcessRunner
{
    private readonly ILogger<SafeProcessRunner> _logger;

    public SafeProcessRunner(ILogger<SafeProcessRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Ruta absoluta a un binario de <c>System32</c>, para no depender de <c>PATH</c>.</summary>
    /// <example><c>System32("fsutil.exe")</c></example>
    public static string System32(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (Path.IsPathRooted(executableName) || executableName.Contains('\\') || executableName.Contains('/'))
        {
            throw new ArgumentException(
                "Se espera solo el nombre del ejecutable, sin ruta.", nameof(executableName));
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            executableName);
    }

    public async Task<ProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!Path.IsPathRooted(executablePath))
        {
            throw new ArgumentException(
                $"'{executablePath}' no es una ruta absoluta. Resolver por PATH permite secuestrar la llamada.",
                nameof(executablePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,      // nada de cmd.exe
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in arguments)
        {
            // El escaping por argumento lo hace el runtime.
            startInfo.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };

        // Lectura por eventos y no ReadToEnd(): leer un stream hasta el final mientras el otro se
        // llena produce deadlock cuando el hijo escribe mucho, y DISM escribe muchísimo.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        _logger.LogInformation(
            "Ejecutando {Exe} con {Count} argumento(s), timeout {Timeout}.",
            executablePath, arguments.Count, timeout);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Distinguir "se acabó el tiempo" de "el usuario canceló": lo segundo se propaga.
            timedOut = !ct.IsCancellationRequested;

            TryKill(process, executablePath);

            if (!timedOut)
            {
                throw;
            }
        }

        stopwatch.Stop();

        int? exitCode = null;
        if (!timedOut)
        {
            // WaitForExitAsync vuelve cuando el proceso terminó, pero los callbacks de stdout/stderr
            // pueden seguir en vuelo. WaitForExit() sin argumentos los drena.
            try
            {
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "No se pudo leer el código de salida de {Exe}.", executablePath);
            }
        }

        var result = new ProcessResult(
            exitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            stopwatch.Elapsed);

        if (timedOut)
        {
            _logger.LogError(
                "{Exe} excedió el timeout de {Timeout} y fue terminado.", executablePath, timeout);
        }
        else if (!result.Succeeded)
        {
            _logger.LogWarning(
                "{Exe} terminó con código {Code}. stderr: {Error}",
                executablePath, exitCode, Truncate(result.StandardError, 500));
        }

        return result;
    }

    private void TryKill(Process process, string executablePath)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "No se pudo terminar {Exe}.", executablePath);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
