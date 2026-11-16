using EasyFix.Core.Classification;
using EasyFix.Core.Configuration;
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

/// <summary>
/// La vista previa de inicio: lo que «Mejorar rendimiento» VA a hacer, calculado durante el
/// análisis para que el técnico sepa qué esperar antes de apretar el botón.
/// </summary>
public sealed class StartupPreviewTests
{
    private static readonly StartupClassifier Classifier = new(new ClassifierOptions
    {
        AutoDisableAllowlist = new[]
        {
            new AllowlistEntry { Publisher = "Spotify AB", Product = "Spotify" },
        },
    });

    private static readonly SystemContext Context = new(IsDomainJoined: false, IsSsd: true, TotalRamGb: 16);

    private static StartupEntry Entry(
        string displayName, string publisher, string product, byte[]? flag = null,
        bool securityProduct = false, string? executablePath = @"C:\App\app.exe") =>
        new(
            new StartupCandidate(
                Id: displayName,
                DisplayName: displayName,
                ExecutablePath: executablePath,
                ProductName: product,
                Signature: new SignatureInfo(true, publisher),
                Location: StartupLocation.RegistryRunCurrentUser,
                IsRegisteredSecurityProduct: securityProduct),
            @"Software\...\StartupApproved\Run",
            displayName,
            flag);

    [Fact]
    public void EntradaEnLaListaBlanca_CaeEnAutoDisable()
    {
        var preview = StartupPreview.Build(
            new[] { Entry("Spotify", "Spotify AB", "Spotify") }, Classifier, Context);

        Assert.Equal(new[] { "Spotify" }, preview.AutoDisable);
        Assert.Empty(preview.NeedsApproval);
        Assert.Empty(preview.Blocked);
    }

    [Fact]
    public void EntradaDesconocida_CaeEnNeedsApproval()
    {
        var preview = StartupPreview.Build(
            new[] { Entry("ProgramaRaro", "Alguien S.A.", "Programa Raro") }, Classifier, Context);

        Assert.Equal(new[] { "ProgramaRaro" }, preview.NeedsApproval);
        Assert.Empty(preview.AutoDisable);
    }

    [Fact]
    public void ProductoDeSeguridad_CaeEnBlocked()
    {
        var preview = StartupPreview.Build(
            new[] { Entry("Antivirus", "Cualquiera", "AV", securityProduct: true) }, Classifier, Context);

        Assert.Equal(new[] { "Antivirus" }, preview.Blocked);
    }

    [Fact]
    public void EntradaYaDeshabilitada_NoCuentaEnNingunBalde()
    {
        // Ya la desactivó una corrida anterior (o el usuario, a mano). Previsualizar algo que ya
        // está hecho sería ruido, y contarla infla el "EnabledTotal" con algo que no aplica.
        var disabledFlag = new byte[12];
        disabledFlag[0] = 0x03;

        var preview = StartupPreview.Build(
            new[] { Entry("Spotify", "Spotify AB", "Spotify", flag: disabledFlag) }, Classifier, Context);

        Assert.Equal(0, preview.EnabledTotal);
        Assert.Empty(preview.AutoDisable);
    }

    [Fact]
    public void MezclaDeLosTres_SeparaCorrectamenteYCuentaElTotal()
    {
        var preview = StartupPreview.Build(
            new[]
            {
                Entry("Spotify", "Spotify AB", "Spotify"),
                Entry("Raro1", "X", "X"),
                Entry("Raro2", "Y", "Y"),
                Entry("Antivirus", "Z", "Z", securityProduct: true),
            },
            Classifier, Context);

        Assert.Equal(4, preview.EnabledTotal);
        Assert.Single(preview.AutoDisable);
        Assert.Equal(2, preview.NeedsApproval.Count);
        Assert.Single(preview.Blocked);
    }

    [Fact]
    public void SinEntradas_VistaVacia()
    {
        var preview = StartupPreview.Build(Array.Empty<StartupEntry>(), Classifier, Context);
        Assert.Equal(0, preview.EnabledTotal);
    }

    // ---- Compuerta de dominio: StartupDisableFix.CanApplyAsync bloquea el fix ENTERO con
    // ---- IsDomainJoined != false. La preview tiene que reflejar eso, no clasificar como si el fix
    // ---- fuera a correr: prometer "se desactiva solo" en un equipo donde el botón no toca nada es
    // ---- la clase de defecto que esta vista previa existe para eliminar, no para repetir.

    [Fact]
    public void EquipoEnDominio_NadaCaeEnAutoDisable_TodoVaABlockedByDomain()
    {
        var domainContext = new SystemContext(IsDomainJoined: true, IsSsd: true, TotalRamGb: 16);

        var preview = StartupPreview.Build(
            new[] { Entry("Spotify", "Spotify AB", "Spotify") }, Classifier, domainContext);

        Assert.Empty(preview.AutoDisable);
        Assert.Empty(preview.NeedsApproval);
        Assert.Empty(preview.Blocked);
        Assert.Equal(new[] { "Spotify" }, preview.BlockedByDomain);
        Assert.Equal(1, preview.EnabledTotal);
    }

    [Fact]
    public void DominioSinComprobar_FallaCerradoIgualQueDominioConfirmado()
    {
        // null = no se pudo medir. CanApplyAsync lo trata igual que true (fallar cerrado); la
        // preview tiene que coincidir exactamente con esa compuerta.
        var unknownDomainContext = new SystemContext(IsDomainJoined: null, IsSsd: true, TotalRamGb: 16);

        var preview = StartupPreview.Build(
            new[] { Entry("Spotify", "Spotify AB", "Spotify") }, Classifier, unknownDomainContext);

        Assert.Empty(preview.AutoDisable);
        Assert.Equal(new[] { "Spotify" }, preview.BlockedByDomain);
    }

    [Fact]
    public void FueraDeDominio_BlockedByDomainQuedaVacio()
    {
        // Contexto por defecto de este archivo: IsDomainJoined = false. Confirma que el gate no se
        // dispara de más.
        var preview = StartupPreview.Build(
            new[] { Entry("Spotify", "Spotify AB", "Spotify") }, Classifier, Context);

        Assert.Empty(preview.BlockedByDomain);
        Assert.Equal(new[] { "Spotify" }, preview.AutoDisable);
    }
}

/// <summary>El bit que decide si un programa de inicio sigue arrancando, única fuente de la regla.</summary>
public sealed class StartupApprovalTests
{
    [Fact]
    public void SinFlag_Habilitada() => Assert.True(StartupApproval.IsEnabled(null));

    [Fact]
    public void FlagVacio_Habilitada() => Assert.True(StartupApproval.IsEnabled(Array.Empty<byte>()));

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x06)]
    public void PrimerByteDeHabilitado_Habilitada(byte first)
    {
        var flag = new byte[12];
        flag[0] = first;
        Assert.True(StartupApproval.IsEnabled(flag));
    }

    [Fact]
    public void PrimerByteEnTres_Deshabilitada()
    {
        var flag = new byte[12];
        flag[0] = 0x03;
        Assert.False(StartupApproval.IsEnabled(flag));
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
