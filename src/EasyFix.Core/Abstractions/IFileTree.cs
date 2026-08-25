namespace EasyFix.Core.Abstractions;

/// <summary>
/// Puerto mínimo sobre el sistema de archivos: exactamente las operaciones que necesita el
/// limpiador de temporales, y ninguna más.
/// </summary>
/// <remarks>
/// Deliberadamente NO expone un borrado recursivo. <c>Directory.Delete(path, recursive: true)</c>
/// sigue junctions y reparse points, y <c>%TEMP%</c> es justo donde los instaladores los dejan.
/// Un junction hacia <c>C:\Users\...\Documents</c> convertiría una limpieza en borrado de datos del
/// cliente. Si la operación peligrosa no existe en el puerto, nadie la puede llamar por descuido.
/// </remarks>
public interface IFileTree
{
    bool DirectoryExists(string path);

    /// <summary>Subdirectorios inmediatos. No recursivo, a propósito.</summary>
    IEnumerable<string> EnumerateDirectories(string path);

    /// <summary>Archivos inmediatos. No recursivo, a propósito.</summary>
    IEnumerable<string> EnumerateFiles(string path);

    /// <summary>
    /// Atributos de un archivo o directorio. El consumidor verifica
    /// <see cref="FileAttributes.ReparsePoint"/> antes de entrar a cualquier directorio.
    /// </summary>
    FileAttributes GetAttributes(string path);

    DateTime GetLastWriteTimeUtc(string path);

    long GetFileLength(string path);

    void DeleteFile(string path);

    /// <summary>
    /// Borra un directorio VACÍO, o el enlace en sí cuando es un reparse point. Nunca recursivo.
    /// </summary>
    void DeleteDirectory(string path);

    /// <summary>Canonicaliza la ruta: resuelve <c>..</c>, <c>.</c> y separadores.</summary>
    string GetFullPath(string path);
}
