using System.Buffers;
using System.Text;

namespace EasyFix.Core.Rollback;

/// <summary>
/// Escribe el journal en un archivo <c>.jsonl</c> bajo <c>%ProgramData%\EasyFix\runs\</c>.
/// </summary>
/// <remarks>
/// <para><b>Sin buffering.</b> <c>AutoFlush</c> más <see cref="FileStream.Flush(bool)"/> con
/// <c>flushToDisk: true</c> en cada línea. Es más lento, y es el punto: la garantía de "escribir
/// antes de actuar" no vale nada si la línea quedó en un buffer del sistema operativo cuando el
/// equipo se apagó.</para>
///
/// <para><b>En ProgramData, no junto al .exe.</b> La app corre desde un USB que se retira; el journal
/// tiene que sobrevivir en el disco del cliente para que "Deshacer todo" siga funcionando en la
/// próxima visita.</para>
/// </remarks>
public sealed class FileJournalSink : IJournalSink, IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private bool _disposed;

    public FileJournalSink(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FilePath = filePath;

        // FileShare.Read para poder mirar el journal con otra herramienta mientras la app corre.
        _stream = new FileStream(
            filePath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, useAsync: false);

        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public string FilePath { get; }

    /// <summary>Carpeta por defecto de los journals: <c>%ProgramData%\EasyFix\runs</c>.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EasyFix",
        "runs");

    /// <summary>
    /// Caracteres prohibidos en un nombre de archivo de Windows, listados de forma explícita.
    /// </summary>
    /// <remarks>
    /// A propósito <b>no</b> se usa <see cref="Path.GetInvalidFileNameChars"/>: ese método devuelve
    /// el set del sistema donde corre el proceso. En Unix devuelve solo <c>'\0'</c> y <c>'/'</c>, así
    /// que <c>':'</c> —que aparece en cualquier timestamp ISO usado como runId— pasaría sin sanear y
    /// el nombre resultaría inválido al llegar a Windows. Para una app de Windows, el set tiene que
    /// ser el de Windows siempre, y así además el comportamiento es verificable desde cualquier
    /// sistema.
    /// </remarks>
    private static readonly SearchValues<char> WindowsInvalidFileNameChars =
        SearchValues.Create("<>:\"/\\|?*");

    /// <summary>Ruta del journal de una corrida. El <paramref name="runId"/> se sanea: va a un nombre de archivo.</summary>
    public static string PathForRun(string runId, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        string safe = string.Concat(runId.Select(
            c => WindowsInvalidFileNameChars.Contains(c) || char.IsControl(c) ? '_' : c));

        return Path.Combine(directory ?? DefaultDirectory, safe + ".jsonl");
    }

    public void WriteLine(string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _writer.WriteLine(json);
        // AutoFlush baja al FileStream; esto baja al disco.
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Dispose();   // dispone también el FileStream
    }
}
