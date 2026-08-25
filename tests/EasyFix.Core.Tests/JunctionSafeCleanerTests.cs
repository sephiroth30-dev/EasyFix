using EasyFix.Core.Cleaning;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El test central es <see cref="JunctionDentroDeTemp_NoSeSigue_YLosDatosDelClienteSobreviven"/>:
/// es el escenario en el que un borrado recursivo ingenuo destruye los documentos del cliente.
/// </summary>
public sealed class JunctionSafeCleanerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MinAge = TimeSpan.FromMinutes(60);
    private const string Temp = @"C:\Users\bob\AppData\Local\Temp";

    private static JunctionSafeCleaner Build(InMemoryFileTree tree) =>
        new(tree, new FixedTimeProvider(Now), NullLogger<JunctionSafeCleaner>.Instance);

    /// <summary>Fecha lo bastante vieja como para ser borrable.</summary>
    private static DateTime Old => Now.UtcDateTime.AddHours(-5);

    /// <summary>Fecha demasiado reciente: podría ser un instalador en curso.</summary>
    private static DateTime Recent => Now.UtcDateTime.AddMinutes(-5);

    [Fact]
    public void JunctionDentroDeTemp_NoSeSigue_YLosDatosDelClienteSobreviven()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\basura.tmp", 2048, Old)
            // El junction: %TEMP%\enlace -> C:\Users\bob\Documents
            .AddDirectory($@"{Temp}\enlace", isReparsePoint: true)
            .AddFile($@"{Temp}\enlace\tesis.docx", 5_000_000, Old)
            .AddFile($@"{Temp}\enlace\fotos\boda.jpg", 8_000_000, Old);

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        // El enlace se quitó...
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.Contains($@"{Temp}\enlace", tree.DeletedDirectories);

        // ...pero NADA de su contenido se tocó.
        Assert.Contains($@"{Temp}\enlace\tesis.docx", tree.RemainingFiles);
        Assert.Contains($@"{Temp}\enlace\fotos\boda.jpg", tree.RemainingFiles);
        Assert.DoesNotContain($@"{Temp}\enlace\tesis.docx", tree.DeletedFiles);

        // Y la basura real sí se borró.
        Assert.Contains($@"{Temp}\basura.tmp", tree.DeletedFiles);
        Assert.Equal(2048, result.FreedBytes);
    }

    [Fact]
    public void ArchivosRecientes_SeSaltean()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\viejo.tmp", 1000, Old)
            .AddFile($@"{Temp}\instalador-en-curso.tmp", 9999, Recent);

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.FilesSkippedTooNew);
        Assert.Contains($@"{Temp}\instalador-en-curso.tmp", tree.RemainingFiles);
        Assert.Equal(1000, result.FreedBytes);
    }

    [Fact]
    public void ArchivoEnUso_SeCuenta_YNoAbortaLaLimpieza()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\bloqueado.log", 500, Old)
            .AddFile($@"{Temp}\libre.tmp", 700, Old);
        tree.LockedPaths.Add($@"{Temp}\bloqueado.log");

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(1, result.FilesSkippedInUse);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(700, result.FreedBytes);
        Assert.Empty(result.Errors); // un archivo en uso es normal, no un error
    }

    [Fact]
    public void LaCarpetaRaiz_NuncaSeBorra()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\a.tmp", 100, Old);

        Build(tree).Clean(Temp, MinAge);

        // Windows y muchas apps asumen que %TEMP% existe.
        Assert.DoesNotContain(Temp, tree.DeletedDirectories);
        Assert.Contains(Temp, tree.RemainingDirectories);
    }

    [Fact]
    public void SubdirectoriosVacios_SeBorranDeAdentroHaciaAfuera()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\a\b\c\hondo.tmp", 100, Old);

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(3, result.DirectoriesDeleted); // c, b, a
        Assert.DoesNotContain($@"{Temp}\a", tree.RemainingDirectories);
    }

    [Fact]
    public void DirectorioIlegible_NoAbortaElResto()
    {
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\ok\bien.tmp", 300, Old)
            .AddFile($@"{Temp}\prohibido\algo.tmp", 400, Old);
        tree.UnreadableDirs.Add($@"{Temp}\prohibido");

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Contains($@"{Temp}\prohibido\algo.tmp", tree.RemainingFiles);
    }

    [Fact]
    public void AtributosIlegibles_HacenQueNoSeEntreAlDirectorio()
    {
        // Sin poder leer los atributos no se puede saber si es un enlace. Fallar cerrado.
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\dudoso\adentro.tmp", 600, Old);
        tree.UnreadableAttributes.Add($@"{Temp}\dudoso");

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Contains($@"{Temp}\dudoso\adentro.tmp", tree.RemainingFiles);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void SymlinkDeArchivo_SeBorraElEnlace_YNoSeCuentaSuTamano()
    {
        // El "tamaño" de un symlink no es espacio recuperado: reportarlo infla la cifra que se le
        // muestra al cliente.
        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\enlace.lnk", 4_000_000, Old, isReparsePoint: true);

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(1, result.ReparsePointsSkipped);
    }

    [Fact]
    public void CarpetaInexistente_DevuelveVacio_SinExcepcion()
    {
        CleanResult result = Build(new InMemoryFileTree()).Clean(@"C:\no\existe", MinAge);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Cancelacion_SePropaga()
    {
        var tree = new InMemoryFileTree().AddDirectory(Temp).AddFile($@"{Temp}\a.tmp", 100, Old);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Build(tree).Clean(Temp, MinAge, cts.Token));
    }

    [Fact]
    public void FreedMegabytes_ConvierteBytesAMegabytes()
    {
        // 3 MiB exactos, para que la aritmética del test sea evidente.
        const long ThreeMebibytes = 3L * 1024 * 1024;

        var tree = new InMemoryFileTree()
            .AddDirectory(Temp)
            .AddFile($@"{Temp}\grande.tmp", ThreeMebibytes, Old);

        CleanResult result = Build(tree).Clean(Temp, MinAge);

        Assert.Equal(ThreeMebibytes, result.FreedBytes);
        Assert.Equal(3.0, result.FreedMegabytes);
    }
}
