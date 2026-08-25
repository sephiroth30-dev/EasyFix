using EasyFix.Core.Abstractions;
using EasyFix.Core.Safety;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Cleaning;

/// <param name="FreedBytes">Bytes efectivamente liberados.</param>
/// <param name="FilesDeleted">Archivos borrados.</param>
/// <param name="FilesSkippedTooNew">Archivos salteados por ser más nuevos que la edad mínima.</param>
/// <param name="FilesSkippedInUse">Archivos en uso por otro proceso.</param>
/// <param name="ReparsePointsSkipped">Junctions/symlinks encontrados: se borró el enlace, nunca su destino.</param>
/// <param name="DirectoriesDeleted">Directorios vacíos eliminados.</param>
/// <param name="Errors">Errores por ruta. Se reportan, no se esconden.</param>
public sealed record CleanResult(
    long FreedBytes,
    int FilesDeleted,
    int FilesSkippedTooNew,
    int FilesSkippedInUse,
    int ReparsePointsSkipped,
    int DirectoriesDeleted,
    IReadOnlyList<string> Errors)
{
    public static CleanResult Empty { get; } = new(0, 0, 0, 0, 0, 0, Array.Empty<string>());

    public double FreedMegabytes => Math.Round(FreedBytes / 1024d / 1024d, 1);
}

/// <summary>
/// Borra el contenido de una carpeta de temporales sin seguir junctions ni symlinks.
/// </summary>
/// <remarks>
/// <para><b>Por qué no <c>Directory.Delete(path, recursive: true)</c>:</b> ese método atraviesa
/// reparse points. <c>%TEMP%</c> es justo donde los instaladores dejan junctions, y uno que apunte a
/// <c>C:\Users\...\Documents</c> convierte una limpieza en borrado de los datos del cliente. Acá los
/// reparse points se borran <i>como enlace</i> y nunca se entra en ellos.</para>
///
/// <para><b>Edad mínima:</b> los archivos recién tocados se saltean porque puede haber un instalador
/// corriendo en ese momento; borrarle sus temporales lo hace fallar a mitad.</para>
///
/// <para><b>La raíz nunca se borra</b>, solo su contenido: Windows y muchas apps asumen que
/// <c>%TEMP%</c> existe.</para>
/// </remarks>
public sealed class JunctionSafeCleaner
{
    private readonly IFileTree _tree;
    private readonly TimeProvider _time;
    private readonly ILogger<JunctionSafeCleaner> _logger;

    public JunctionSafeCleaner(IFileTree tree, TimeProvider time, ILogger<JunctionSafeCleaner> logger)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _tree = tree;
        _time = time;
        _logger = logger;
    }

    public CleanResult Clean(string root, TimeSpan minAge, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string canonicalRoot;
        try
        {
            canonicalRoot = _tree.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CleanResult.Empty with { Errors = new[] { $"Ruta inválida '{root}': {ex.Message}" } };
        }

        if (!_tree.DirectoryExists(canonicalRoot))
        {
            _logger.LogInformation("No existe {Root}; nada que limpiar.", canonicalRoot);
            return CleanResult.Empty;
        }

        DateTime cutoffUtc = _time.GetUtcNow().UtcDateTime - minAge;

        long freed = 0;
        int filesDeleted = 0, tooNew = 0, inUse = 0, reparse = 0, dirsDeleted = 0;
        var errors = new List<string>();

        // Recorrido iterativo con pila: %TEMP% puede tener árboles muy profundos (node_modules,
        // caches de instaladores) y la recursión ahí termina en StackOverflow, que no se puede
        // atrapar.
        var pending = new Stack<string>();
        var visitedDirs = new List<string>(); // en pre-orden, para borrar después al revés
        pending.Push(canonicalRoot);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            visitedDirs.Add(dir);

            // --- Archivos de este directorio ---
            foreach (string file in SafeEnumerate(() => _tree.EnumerateFiles(dir), dir, errors))
            {
                ct.ThrowIfCancellationRequested();

                if (!PathGuard.IsWithin(file, canonicalRoot))
                {
                    // Defensa en profundidad: no se atraviesan reparse points, pero si una ruta
                    // igual apareció fuera de la raíz, no se toca.
                    errors.Add($"Se ignoró '{file}': cae fuera de {canonicalRoot}.");
                    continue;
                }

                try
                {
                    if (_tree.GetLastWriteTimeUtc(file) > cutoffUtc)
                    {
                        tooNew++;
                        continue;
                    }

                    // Un archivo puede ser un symlink: se borra el enlace (DeleteFile no sigue al
                    // destino) y no se cuenta su tamaño, que no es espacio real.
                    bool isLink = (_tree.GetAttributes(file) & FileAttributes.ReparsePoint) != 0;
                    long size = isLink ? 0 : _tree.GetFileLength(file);

                    _tree.DeleteFile(file);

                    filesDeleted++;
                    freed += size;
                    if (isLink)
                    {
                        reparse++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Archivo abierto por otro proceso. Es lo normal en %TEMP%, no es un error.
                    inUse++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{file}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // --- Subdirectorios ---
            foreach (string sub in SafeEnumerate(() => _tree.EnumerateDirectories(dir), dir, errors))
            {
                ct.ThrowIfCancellationRequested();

                if (!PathGuard.IsWithin(sub, canonicalRoot))
                {
                    errors.Add($"Se ignoró '{sub}': cae fuera de {canonicalRoot}.");
                    continue;
                }

                bool isReparsePoint;
                try
                {
                    isReparsePoint = (_tree.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex)
                {
                    // Sin poder leer los atributos no se puede saber si es un enlace → no se entra.
                    errors.Add($"{sub}: no se pudieron leer los atributos ({ex.GetType().Name}); se omite.");
                    continue;
                }

                if (isReparsePoint)
                {
                    // ACÁ ESTÁ EL PUNTO DE TODA LA CLASE: se borra el enlace, NO se entra.
                    reparse++;
                    _logger.LogInformation(
                        "Reparse point en {Path}: se borra el enlace sin entrar en su destino.", sub);
                    try
                    {
                        _tree.DeleteDirectory(sub); // no recursivo → quita el enlace
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        inUse++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{sub}: {ex.GetType().Name}: {ex.Message}");
                    }

                    continue;
                }

                pending.Push(sub);
            }
        }

        // --- Borrar directorios vacíos, de más profundo a más superficial ---
        // La raíz (índice 0) se salta a propósito: %TEMP% tiene que seguir existiendo.
        for (int i = visitedDirs.Count - 1; i >= 1; i--)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _tree.DeleteDirectory(visitedDirs[i]);
                dirsDeleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No estaba vacío (quedaron archivos en uso) o está bloqueado. Normal.
            }
            catch (Exception ex)
            {
                errors.Add($"{visitedDirs[i]}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Limpieza de {Root}: {Mb} MB liberados, {Deleted} archivos, {InUse} en uso, {Reparse} enlaces, {Dirs} carpetas.",
            canonicalRoot, Math.Round(freed / 1024d / 1024d, 1), filesDeleted, inUse, reparse, dirsDeleted);

        return new CleanResult(freed, filesDeleted, tooNew, inUse, reparse, dirsDeleted, errors);
    }

    /// <summary>
    /// Materializa una enumeración perezosa: <c>EnumerateFiles</c> puede tirar excepción a mitad del
    /// recorrido (permisos, carpeta borrada por otro proceso) y en un <c>foreach</c> eso mata el
    /// bucle entero. Acá se pierde solo ese directorio.
    /// </summary>
    private static IReadOnlyList<string> SafeEnumerate(
        Func<IEnumerable<string>> enumerate,
        string dir,
        List<string> errors)
    {
        try
        {
            return enumerate().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            errors.Add($"{dir}: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
