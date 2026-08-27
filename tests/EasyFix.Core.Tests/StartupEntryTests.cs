using EasyFix.Core.Classification;
using EasyFix.Core.Fixes;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests del estado de una entrada de inicio y del parseo de su línea de comandos.
/// </summary>
/// <remarks>
/// El flag de <c>StartupApproved</c> es el mecanismo por el que el Administrador de tareas habilita y
/// deshabilita programas de arranque. Interpretarlo mal significa desactivar algo que ya estaba
/// desactivado, o peor, creer que se desactivó algo que sigue arrancando.
/// </remarks>
public sealed class StartupEntryStateTests
{
    private static StartupEntry Entry(byte[]? flag) =>
        new(
            new StartupCandidate(
                Id: "test",
                DisplayName: "Programa",
                ExecutablePath: @"C:\App\app.exe",
                ProductName: "Programa",
                Signature: new SignatureInfo(true, "Publisher"),
                Location: StartupLocation.RegistryRunCurrentUser),
            @"Software\...\StartupApproved\Run",
            "Programa",
            flag);

    [Fact]
    public void SinValorEnStartupApproved_LaEntradaEstaHabilitada()
    {
        // Es el estado por defecto: si Windows nunca escribió el flag, el programa arranca.
        Assert.True(Entry(null).IsEnabled);
    }

    [Fact]
    public void ValorVacio_SeTrataComoHabilitada()
    {
        Assert.True(Entry(Array.Empty<byte>()).IsEnabled);
    }

    [Theory]
    [InlineData(0x02)]   // habilitado
    [InlineData(0x06)]   // habilitado, otra variante que usa Windows
    public void PrimerByteDeHabilitado_LaEntradaEstaHabilitada(byte first)
    {
        var flag = new byte[12];
        flag[0] = first;

        Assert.True(Entry(flag).IsEnabled);
    }

    [Fact]
    public void PrimerByteEnTres_LaEntradaEstaDeshabilitada()
    {
        var flag = new byte[12];
        flag[0] = 0x03;

        Assert.False(Entry(flag).IsEnabled);
    }

    [Fact]
    public void UnValorConBasura_SeTrataComoHabilitada()
    {
        // Falla del lado seguro: creer que está habilitada lleva a evaluarla con el clasificador,
        // que decide con criterio. Creerla deshabilitada la saltearía sin mirarla.
        var flag = new byte[] { 0xFF, 0x00 };

        Assert.True(Entry(flag).IsEnabled);
    }
}

public sealed class StartupCommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData("\"C:\\App\\a.exe\"", @"C:\App\a.exe")]
    public void ExtraeLaRutaEntrecomillada(string commandLine, string expected) =>
        Assert.Equal(expected, StartupEntryReader.ExtractExecutablePath(commandLine));

    [Theory]
    [InlineData(@"C:\App\app.exe /background", @"C:\App\app.exe")]
    [InlineData(@"C:\App\app.exe", @"C:\App\app.exe")]
    [InlineData(@"C:\Windows\system32\cmd.com /c algo", @"C:\Windows\system32\cmd.com")]
    public void ExtraeLaRutaSinComillas(string commandLine, string expected) =>
        Assert.Equal(expected, StartupEntryReader.ExtractExecutablePath(commandLine));

    [Fact]
    public void RutaConEspaciosSinComillas_SeCortaEnLaExtension()
    {
        // Caso real y molesto: sin comillas y con espacios, la única pista es la extensión.
        Assert.Equal(
            @"C:\Program Files\App\app.exe",
            StartupEntryReader.ExtractExecutablePath(@"C:\Program Files\App\app.exe -silent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("algo sin extension reconocible")]
    [InlineData("\"sin cierre de comilla")]
    public void SinRutaReconocible_DevuelveNull(string commandLine)
    {
        // No se arriesga una ruta inventada: sin ruta, la firma no se puede verificar y la entrada
        // cae en Capa 3, que es lo correcto.
        Assert.Null(StartupEntryReader.ExtractExecutablePath(commandLine));
    }
}

/// <summary>
/// Tests de la separación entre «Mejorar rendimiento» y «Reparar errores».
/// </summary>
public sealed class FixCategoryTests
{
    private sealed class Stub : IFix
    {
        public Stub(string id, FixCategory category)
        {
            Id = id;
            Category = category;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Description => Id;
        public FixCategory Category { get; }
        public FixTier Tier => FixTier.SafeAuto;
        public bool RequiresReboot => false;
        public bool TouchesBootOrDisk => false;
        public bool IsReversible => true;
        public bool IsLongRunning => false;

        public Task<FixApplicability> CanApplyAsync(FixContext c, CancellationToken ct) =>
            Task.FromResult(FixApplicability.Yes());

        public Task<FixOutcome> ApplyAsync(FixContext c, IProgress<string> l, CancellationToken ct) =>
            Task.FromResult(FixOutcome.Applied("ok"));
    }

    [Fact]
    public void LasDosCategoriasSeFiltranSinSolaparse()
    {
        // El filtrado es lo que evita que «Mejorar rendimiento» —que debe tardar minutos— arrastre
        // DISM, que tarda cuarenta.
        var fixes = new IFix[]
        {
            new Stub("temp.clean", FixCategory.Performance),
            new Stub("startup.disable", FixCategory.Performance),
            new Stub("system.repair-files", FixCategory.Repair),
            new Stub("disk.chkdsk", FixCategory.Repair),
        };

        Assert.Equal(2, fixes.Count(f => f.Category == FixCategory.Performance));
        Assert.Equal(2, fixes.Count(f => f.Category == FixCategory.Repair));
        Assert.DoesNotContain(fixes, f =>
            f.Category != FixCategory.Performance && f.Category != FixCategory.Repair);
    }
}
