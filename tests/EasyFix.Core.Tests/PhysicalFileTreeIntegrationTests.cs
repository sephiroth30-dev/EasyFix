using EasyFix.Core.Cleaning;
using EasyFix.Core.Rollback;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests contra el filesystem real, no contra el fake.
/// </summary>
/// <remarks>
/// <para>El test del junction con <see cref="InMemoryFileTree"/> prueba que el cleaner respeta el
/// atributo <see cref="FileAttributes.ReparsePoint"/>. Lo que no prueba es que el atributo se lea
/// bien de un enlace de verdad. Acá se crea un symlink real: .NET marca los symlinks con
/// <c>ReparsePoint</c> tanto en Windows como en Unix, así que el test corre en los dos.</para>
///
/// <para>Un fake que coincide con una implementación equivocada da confianza falsa. Esta es la única
/// forma de descartarlo sin una VM.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PhysicalFileTreeIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly PhysicalFileTree _tree = new();

    public PhysicalFileTreeIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "easyfix-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpieza best-effort: es una carpeta temporal.
        }
    }

    private JunctionSafeCleaner BuildCleaner(DateTimeOffset now) =>
        new(_tree, new FixedTimeProvider(now), NullLogger<JunctionSafeCleaner>.Instance);

    [Fact]
    public void UnSymlinkRealSeMarcaComoReparsePoint()
    {
        // Si esto falla, toda la defensa del cleaner se cae: se apoya en este atributo.
        string target = Path.Combine(_root, "destino");
        string link = Path.Combine(_root, "enlace");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        FileAttributes attributes = _tree.GetAttributes(link);

        Assert.True((attributes & FileAttributes.ReparsePoint) != 0,
            "El enlace real no trae FileAttributes.ReparsePoint; la protección del cleaner no funcionaría.");
    }

    [Fact]
    public void ElCleanerNoSigueUnSymlinkReal_YLosDatosDelDestinoSobreviven()
    {
        // Estructura:
        //   <root>/temp/basura.tmp          -> se borra
        //   <root>/temp/enlace -> <root>/documentos
        //   <root>/documentos/tesis.docx    -> TIENE que sobrevivir
        string temp = Path.Combine(_root, "temp");
        string documents = Path.Combine(_root, "documentos");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(documents);

        string thesis = Path.Combine(documents, "tesis.docx");
        File.WriteAllText(thesis, "el trabajo de meses del cliente");

        string junk = Path.Combine(temp, "basura.tmp");
        File.WriteAllText(junk, new string('x', 4096));

        Directory.CreateSymbolicLink(Path.Combine(temp, "enlace"), documents);

        // Envejecer los archivos para que pasen el filtro de edad mínima.
        DateTime old = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(junk, old);
        File.SetLastWriteTimeUtc(thesis, old);

        CleanResult result = BuildCleaner(DateTimeOffset.UtcNow)
            .Clean(temp, TimeSpan.FromMinutes(60));

        // El archivo del cliente sigue intacto, con su contenido.
        Assert.True(File.Exists(thesis), "El cleaner siguió el enlace y borró datos del cliente.");
        Assert.Equal("el trabajo de meses del cliente", File.ReadAllText(thesis));
        Assert.True(Directory.Exists(documents));

        // La basura sí se fue, y el enlace también.
        Assert.False(File.Exists(junk));
        Assert.Equal(1, result.ReparsePointsSkipped);
        Assert.Equal(4096, result.FreedBytes);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ArchivoDeSoloLectura_SeBorraIgual()
    {
        // En %TEMP% aparecen seguido: instaladores que copian preservando atributos.
        string file = Path.Combine(_root, "solo-lectura.tmp");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        _tree.DeleteFile(file);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DeleteDirectory_ConContenido_Falla_PorqueNoEsRecursivo()
    {
        // Garantía estructural: el puerto no ofrece borrado recursivo, así que nadie puede arrasar
        // un árbol siguiendo un enlace.
        string dir = Path.Combine(_root, "conCosas");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "algo.txt"), "x");

        Assert.Throws<IOException>(() => _tree.DeleteDirectory(dir));
        Assert.True(Directory.Exists(dir));
    }
}

/// <summary>Tests del journal contra un archivo real.</summary>
[Trait("Category", "Integration")]
public sealed class FileJournalSinkIntegrationTests : IDisposable
{
    private readonly string _dir;

    public FileJournalSinkIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "easyfix-journal-" + Guid.NewGuid().ToString("N")[..12]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EscribeYSeVuelveALeerDesdeDisco()
    {
        string path = FileJournalSink.PathForRun("2026-08-24T20-14-33", _dir);
        var header = new JournalHeader { RunId = "r1", RestorePointSequence = 42 };

        using (var sink = new FileJournalSink(path))
        {
            RunJournal journal = RunJournal.Start(header, sink);
            journal.Append(new JournalAction
            {
                FixId = "temp.clean",
                Target = @"C:\Windows\Temp",
                Reversible = false,
                FreedBytes = 4096,
            });
            journal.SetState(RunState.Completed);
        }

        // Releer desde el archivo, no desde memoria: es lo que hace "Deshacer todo" en otra sesión.
        LoadedJournal loaded = JournalReader.Parse(File.ReadLines(path));

        Assert.True(loaded.IsUsable);
        Assert.Equal(42, loaded.Header!.RestorePointSequence);
        Assert.Single(loaded.Actions);
        Assert.False(loaded.Actions[0].Reversible);
        Assert.Equal(RunState.Completed, loaded.State);
        Assert.Empty(loaded.CorruptLines);
    }

    [Fact]
    public void CadaLineaQuedaEnDiscoAlEscribirse_SinEsperarElDispose()
    {
        // La garantía de "escribir antes de actuar" no vale nada si la línea quedó en un buffer
        // cuando el equipo se apagó. Se lee el archivo con el sink todavía abierto.
        string path = FileJournalSink.PathForRun("flush-test", _dir);

        using var sink = new FileJournalSink(path);
        RunJournal journal = RunJournal.Start(new JournalHeader { RunId = "r1" }, sink);
        journal.Append(new JournalAction { FixId = "a", Target = "t", Reversible = true });

        string[] lines = File.ReadAllLines(path);

        Assert.Equal(2, lines.Length); // encabezado + acción, ya en disco
    }

    [Fact]
    public void CreaLaCarpetaSiNoExiste()
    {
        string nested = Path.Combine(_dir, "a", "b", "c");
        string path = FileJournalSink.PathForRun("run", nested);

        using var sink = new FileJournalSink(path);

        Assert.True(Directory.Exists(nested));
    }

    [Theory]
    [InlineData("2026-08-24T20:14:33", "2026-08-24T20_14_33.jsonl")]  // ':' del timestamp ISO
    [InlineData("run<1>", "run_1_.jsonl")]
    [InlineData("a/b\\c", "a_b_c.jsonl")]
    [InlineData("pipe|quote\"star*", "pipe_quote_star_.jsonl")]
    [InlineData("normal-123_ok", "normal-123_ok.jsonl")]
    public void PathForRun_SaneaConElSetDeWindows_EnCualquierSistema(string runId, string expectedFileName)
    {
        // Usa el set de caracteres inválidos de Windows de forma explícita, no el de la plataforma
        // donde corre el test. Por eso esta aserción vale igual en macOS y en Windows.
        string path = FileJournalSink.PathForRun(runId, _dir);

        Assert.Equal(expectedFileName, Path.GetFileName(path));
    }

    [Fact]
    public void EscribirDespuesDeDispose_Tira()
    {
        string path = FileJournalSink.PathForRun("disposed", _dir);
        var sink = new FileJournalSink(path);
        sink.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sink.WriteLine("{}"));
    }
}
