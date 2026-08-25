using EasyFix.Core.Abstractions;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>
/// Árbol de archivos en memoria, con rutas estilo Windows.
/// </summary>
/// <remarks>
/// Puede simular lo que importa de verdad acá: reparse points, archivos bloqueados por otro proceso,
/// y directorios que tiran excepción al enumerarlos. Sin esto no hay forma de probar el caso del
/// junction sin crear un junction real, que necesita Windows y privilegios.
/// </remarks>
public sealed class InMemoryFileTree : IFileTree
{
    private sealed record Entry(long Length, DateTime LastWriteUtc, FileAttributes Attributes);

    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rutas que tiran <see cref="IOException"/> al intentar borrarlas ("archivo en uso").</summary>
    public HashSet<string> LockedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directorios que tiran <see cref="UnauthorizedAccessException"/> al enumerarse.</summary>
    public HashSet<string> UnreadableDirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rutas cuyos atributos no se pueden leer.</summary>
    public HashSet<string> UnreadableAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> DeletedFiles { get; } = new();
    public List<string> DeletedDirectories { get; } = new();

    // --- Construcción -----------------------------------------------------------------------

    public InMemoryFileTree AddDirectory(string path, bool isReparsePoint = false)
    {
        string p = Normalize(path);
        _dirs[p] = new Entry(
            0,
            DateTime.UnixEpoch,
            FileAttributes.Directory | (isReparsePoint ? FileAttributes.ReparsePoint : 0));

        // Los ancestros existen implícitamente, como en un filesystem real.
        string? parent = ParentOf(p);
        while (parent is not null && !_dirs.ContainsKey(parent))
        {
            _dirs[parent] = new Entry(0, DateTime.UnixEpoch, FileAttributes.Directory);
            parent = ParentOf(parent);
        }

        return this;
    }

    public InMemoryFileTree AddFile(
        string path,
        long length = 1024,
        DateTime? lastWriteUtc = null,
        bool isReparsePoint = false)
    {
        string p = Normalize(path);
        _files[p] = new Entry(
            length,
            lastWriteUtc ?? DateTime.UnixEpoch,
            FileAttributes.Normal | (isReparsePoint ? FileAttributes.ReparsePoint : 0));

        string? parent = ParentOf(p);
        if (parent is not null)
        {
            AddDirectory(parent);
        }

        return this;
    }

    // --- IFileTree --------------------------------------------------------------------------

    public bool DirectoryExists(string path) => _dirs.ContainsKey(Normalize(path));

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        string p = Normalize(path);
        if (UnreadableDirs.Contains(p))
        {
            throw new UnauthorizedAccessException($"Acceso denegado a {p}.");
        }

        return _dirs.Keys.Where(d => IsDirectChild(d, p)).ToList();
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        string p = Normalize(path);
        if (UnreadableDirs.Contains(p))
        {
            throw new UnauthorizedAccessException($"Acceso denegado a {p}.");
        }

        return _files.Keys.Where(f => IsDirectChild(f, p)).ToList();
    }

    public FileAttributes GetAttributes(string path)
    {
        string p = Normalize(path);
        if (UnreadableAttributes.Contains(p))
        {
            throw new IOException($"No se pudieron leer los atributos de {p}.");
        }

        if (_files.TryGetValue(p, out Entry? f)) { return f.Attributes; }
        if (_dirs.TryGetValue(p, out Entry? d)) { return d.Attributes; }
        throw new FileNotFoundException(p);
    }

    public DateTime GetLastWriteTimeUtc(string path)
    {
        string p = Normalize(path);
        if (_files.TryGetValue(p, out Entry? f)) { return f.LastWriteUtc; }
        if (_dirs.TryGetValue(p, out Entry? d)) { return d.LastWriteUtc; }
        throw new FileNotFoundException(p);
    }

    public long GetFileLength(string path) =>
        _files.TryGetValue(Normalize(path), out Entry? f)
            ? f.Length
            : throw new FileNotFoundException(path);

    public void DeleteFile(string path)
    {
        string p = Normalize(path);
        if (LockedPaths.Contains(p))
        {
            throw new IOException($"El archivo {p} está en uso por otro proceso.");
        }

        _files.Remove(p);
        DeletedFiles.Add(p);
    }

    public void DeleteDirectory(string path)
    {
        string p = Normalize(path);
        if (LockedPaths.Contains(p))
        {
            throw new IOException($"El directorio {p} está en uso.");
        }

        bool isReparsePoint = _dirs.TryGetValue(p, out Entry? entry)
                              && (entry.Attributes & FileAttributes.ReparsePoint) != 0;

        // Un reparse point se borra como enlace aunque su destino tenga contenido; un directorio
        // normal solo se borra si está vacío. Es el comportamiento real de Directory.Delete.
        if (!isReparsePoint)
        {
            bool hasChildren = _files.Keys.Any(f => IsDirectChild(f, p))
                            || _dirs.Keys.Any(d => IsDirectChild(d, p));
            if (hasChildren)
            {
                throw new IOException($"El directorio {p} no está vacío.");
            }
        }

        _dirs.Remove(p);
        DeletedDirectories.Add(p);
    }

    public string GetFullPath(string path) => Normalize(path);

    // --- Helpers ----------------------------------------------------------------------------

    /// <summary>Rutas que quedaron en el árbol. Sirve para afirmar qué NO se borró.</summary>
    public IReadOnlyCollection<string> RemainingFiles => _files.Keys;

    public IReadOnlyCollection<string> RemainingDirectories => _dirs.Keys;

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private static string? ParentOf(string normalizedPath)
    {
        int i = normalizedPath.LastIndexOf('\\');
        // "C:" no tiene padre.
        return i <= 2 ? null : normalizedPath[..i];
    }

    private static bool IsDirectChild(string candidate, string parent)
    {
        if (!candidate.StartsWith(parent + '\\', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !candidate[(parent.Length + 1)..].Contains('\\', StringComparison.Ordinal);
    }
}
