using EasyFix.Core.Abstractions;

namespace EasyFix.Core.Cleaning;

/// <summary>
/// Implementación real de <see cref="IFileTree"/> sobre <c>System.IO</c>.
/// </summary>
/// <remarks>
/// Casi todo es delegación directa. Los dos puntos que importan:
/// <list type="bullet">
/// <item><see cref="DeleteDirectory"/> llama a <c>Directory.Delete</c> <b>sin</b> el flag recursivo.
/// Es la garantía estructural de que nadie pueda borrar un árbol siguiendo un junction.</item>
/// <item>Las enumeraciones son perezosas, igual que las de <c>System.IO</c>: el consumidor
/// (<see cref="JunctionSafeCleaner"/>) las materializa dentro de un try/catch porque pueden tirar
/// excepción a mitad del recorrido.</item>
/// </list>
/// </remarks>
public sealed class PhysicalFileTree : IFileTree
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public void DeleteFile(string path)
    {
        // Un archivo de solo lectura tira UnauthorizedAccessException en Delete. En %TEMP% aparecen
        // seguido (instaladores que copian con atributos). Se le quita el flag y se reintenta una vez.
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                throw; // no era el flag de solo lectura: que lo maneje el llamador
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Borra un directorio vacío, o el enlace en sí cuando es un reparse point.
    /// <b>Nunca recursivo</b> — ver la nota de la clase.
    /// </summary>
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
