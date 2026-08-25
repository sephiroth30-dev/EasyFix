using EasyFix.Core.Apps;
using EasyFix.Core.Classification;
using EasyFix.Core.Configuration;
using EasyFix.Core.Processes;
using EasyFix.Core.Safety;
using EasyFix.Core.Tests.Fakes;
using Xunit;

namespace EasyFix.Core.Tests;

public sealed class PathGuardTests
{
    [Theory]
    [InlineData(@"C:\Temp\a\b.txt", @"C:\Temp", true)]
    [InlineData(@"C:\Temp", @"C:\Temp", true)]                 // la raíz está dentro de sí misma
    [InlineData(@"C:\Temp\", @"C:\Temp", true)]                // separador final irrelevante
    [InlineData(@"c:\temp\A.TXT", @"C:\Temp", true)]           // sin distinguir mayúsculas
    [InlineData(@"C:\Temp2\a.txt", @"C:\Temp", false)]         // el caso que rompe un StartsWith pelado
    [InlineData(@"C:\TempEvil\a.txt", @"C:\Temp", false)]
    [InlineData(@"C:\Other\a.txt", @"C:\Temp", false)]
    [InlineData(@"C:/Temp/a.txt", @"C:\Temp", true)]           // separadores mezclados
    public void IsWithin_ComparaPorSegmentoDeRuta(string candidate, string root, bool expected) =>
        Assert.Equal(expected, PathGuard.IsWithin(candidate, root));

    [Theory]
    [InlineData(null, @"C:\Temp")]
    [InlineData("", @"C:\Temp")]
    [InlineData("   ", @"C:\Temp")]
    [InlineData(@"C:\Temp\a", null)]
    [InlineData(@"C:\Temp\a", "")]
    public void IsWithin_ConEntradaVacia_DevuelveFalse(string? candidate, string? root) =>
        Assert.False(PathGuard.IsWithin(candidate!, root!));

    [Fact]
    public void EnsureWithin_FueraDeLaRaiz_Tira()
    {
        UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(
            () => PathGuard.EnsureWithin(@"C:\Users\bob\Documents\tesis.docx", @"C:\Temp"));

        Assert.Contains("fuera de la raíz", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class CertificateSubjectTests
{
    [Fact]
    public void ExtraeLaOrganizacionDeUnSubjectNormal() =>
        Assert.Equal("Spotify AB",
            CertificateSubject.ExtractOrganization("CN=Spotify AB, O=Spotify AB, L=Stockholm, C=SE"));

    [Fact]
    public void PrefiereOSobreCN() =>
        // El CN a veces trae el nombre del producto; la O es la entidad legal que verificó la CA.
        Assert.Equal("Adobe Inc.",
            CertificateSubject.ExtractOrganization("CN=Acrobat Reader, O=Adobe Inc., C=US"));

    [Fact]
    public void SoportaComasDentroDeComillas() =>
        // Un split(',') partiría "Foo, Inc." al medio y devolvería "Foo".
        Assert.Equal("Foo, Inc.",
            CertificateSubject.ExtractOrganization("CN=Thing, O=\"Foo, Inc.\", L=Springfield"));

    [Fact]
    public void SoportaComaEscapada() =>
        Assert.Equal("Bar, LLC",
            CertificateSubject.ExtractOrganization(@"CN=Thing, O=Bar\, LLC, C=US"));

    [Fact]
    public void AceptaMinusculaEnLaClave() =>
        Assert.Equal("Valve Corp.",
            CertificateSubject.ExtractOrganization("cn=Steam, o=Valve Corp."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CN=Solo el CN, L=Nowhere")]   // no hay O=
    [InlineData("O=")]                          // O= vacía
    [InlineData("basura sin signos igual")]
    public void SinOrganizacionUtil_DevuelveNull(string? subject) =>
        Assert.Null(CertificateSubject.ExtractOrganization(subject));
}

public sealed class WingetPackageIdTests
{
    [Theory]
    [InlineData("Google.Chrome")]
    [InlineData("Adobe.Acrobat.Reader.64-bit")]
    [InlineData("7zip.7zip")]
    [InlineData("Microsoft.VCRedist.2015+.x64")]   // el '+' es legítimo
    [InlineData("Notepad++.Notepad++")]
    [InlineData("A")]
    public void IdsRealesSonValidos(string id) => Assert.True(WingetPackageId.IsValid(id));

    [Theory]
    [InlineData("Google.Chrome && calc.exe")]      // intento de inyección
    [InlineData("Google.Chrome; rm -rf /")]
    [InlineData("Google.Chrome | more")]
    [InlineData("--uninstall")]                     // no puede empezar con guion: parecería un flag
    [InlineData(".hidden")]
    [InlineData("con espacio")]
    [InlineData("comillas\"raras")]
    [InlineData("salto\nde-linea")]
    [InlineData("ruta\\con\\barras")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IdsPeligrososOMalFormados_SeRechazan(string? id) => Assert.False(WingetPackageId.IsValid(id));

    [Fact]
    public void Validate_ConIdInvalido_TiraConMensajeUtil()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => WingetPackageId.Validate("Chrome && calc"));

        Assert.Contains("no es un ID de paquete de winget válido", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class SafeProcessRunnerTests
{
    [WindowsOnlyFact]
    public void System32_DevuelveUnaRutaAbsolutaBajoSystem32()
    {
        string path = SafeProcessRunner.System32("fsutil.exe");

        Assert.True(Path.IsPathRooted(path));
        Assert.Contains("System32", path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("fsutil.exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\fsutil.exe")]
    [InlineData(@"..\fsutil.exe")]
    [InlineData("sub/dir/fsutil.exe")]
    public void System32_RechazaCualquierCosaConRuta(string input) =>
        Assert.Throws<ArgumentException>(() => SafeProcessRunner.System32(input));

    [Fact]
    public async Task RunAsync_RechazaRutaRelativa()
    {
        // Resolver por PATH permitiría que un binario homónimo en el directorio actual —en el equipo
        // comprometido que estamos reparando— secuestre la llamada.
        var runner = new SafeProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<SafeProcessRunner>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync("fsutil.exe", Array.Empty<string>(), TimeSpan.FromSeconds(1)));
    }
}

public sealed class OptionsLoaderTests
{
    /// <summary>
    /// Parsea el <c>appsettings.json</c> real del repo. Si alguien renombra una clave del archivo y
    /// no toca el modelo, este test lo agarra.
    /// </summary>
    private static string RealAppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, OptionsLoader.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("No se encontró appsettings.json subiendo desde la salida del build.");
    }

    [Fact]
    public void ParseaElAppSettingsRealDelRepo()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        Assert.Equal(15, options.Thresholds.LowDiskFreePercent);
        Assert.Equal(85, options.Thresholds.CommitPressurePercent);
        Assert.Equal(TimeSpan.FromMinutes(60), options.Thresholds.TempFileMinAge);

        Assert.NotEmpty(options.Classifier.HardBlock.ProtectedCertificatePublishers);
        Assert.NotEmpty(options.Classifier.AutoDisableAllowlist);
        Assert.NotEmpty(options.Bloatware.Candidates);
        Assert.NotEmpty(options.Services.OfferToDisable);
        Assert.NotEmpty(options.WingetPackages);
    }

    [Fact]
    public void TodosLosIdsDeWingetConfigurados_PasanLaValidacion()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        Assert.All(options.WingetPackages, p => Assert.True(
            WingetPackageId.IsValid(p.Id), $"ID inválido en appsettings.json: '{p.Id}'"));
    }

    [Fact]
    public void SysMain_SoloSeOfreceBajoCondicion()
    {
        // En HDD, Superfetch AYUDA. Si esta entrada pierde su condición, la app empezaría a
        // degradar equipos con disco mecánico.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        ServiceCandidate sysMain = Assert.Single(options.Services.OfferToDisable, s => s.Name == "SysMain");

        Assert.Equal("SsdAndRamAtLeast8Gb", sysMain.OnlyIf);
    }

    [Fact]
    public void Spooler_SoloSeOfreceSiNoHayImpresoras()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        ServiceCandidate spooler = Assert.Single(options.Services.OfferToDisable, s => s.Name == "Spooler");

        Assert.Equal("NoPrintersInstalled", spooler.OnlyIf);
    }

    [Fact]
    public void TeamsEnLaListaBlanca_EstaLimitadoAEquiposFueraDeDominio()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        AllowlistEntry teams = Assert.Single(options.Classifier.AutoDisableAllowlist, e => e.Product.Contains("Teams", StringComparison.Ordinal));

        Assert.True(teams.OnlyIfNotDomainJoined);
    }

    /// <summary>
    /// Verifica la lógica de expansión en cualquier sistema, usando una variable que el test define.
    /// </summary>
    /// <remarks>
    /// Es el complemento cross-platform de
    /// <see cref="ProtectedPathPrefixes_ExpandenSystemRoot_EnWindows"/>: prueba que el loader
    /// realmente expande, sin depender de que exista <c>%SystemRoot%</c>.
    /// </remarks>
    [Fact]
    public void ProtectedPathPrefixes_SeExpanden()
    {
        const string VarName = "EASYFIX_TEST_ROOT";
        const string VarValue = "/ruta/de/prueba";

        string? original = Environment.GetEnvironmentVariable(VarName);
        Environment.SetEnvironmentVariable(VarName, VarValue);
        try
        {
            EasyFixOptions options = OptionsLoader.Parse($$"""
                { "Classifier": { "Layer1_HardBlock": {
                    "ProtectedPathPrefixes": [ "%{{VarName}}%/System32" ] } } }
                """);

            string prefix = Assert.Single(options.Classifier.HardBlock.ProtectedPathPrefixes);
            Assert.Equal($"{VarValue}/System32", prefix);
            Assert.DoesNotContain("%", prefix, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, original);
        }
    }

    /// <summary>
    /// El <c>appsettings.json</c> real usa <c>%SystemRoot%</c>, que solo existe en Windows.
    /// </summary>
    [WindowsOnlyFact]
    public void ProtectedPathPrefixes_ExpandenSystemRoot_EnWindows()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(RealAppSettingsPath()));

        Assert.NotEmpty(options.Classifier.HardBlock.ProtectedPathPrefixes);
        Assert.All(options.Classifier.HardBlock.ProtectedPathPrefixes,
            p => Assert.DoesNotContain("%", p, StringComparison.Ordinal));
    }

    [Fact]
    public void JsonVacio_UsaLosDefaults()
    {
        EasyFixOptions options = OptionsLoader.Parse("{}");

        Assert.Equal(15, options.Thresholds.LowDiskFreePercent);
        Assert.Empty(options.Classifier.AutoDisableAllowlist);
        Assert.Empty(options.WingetPackages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void JsonVacioOEnBlanco_Tira(string json) =>
        Assert.Throws<ArgumentException>(() => OptionsLoader.Parse(json));
}

public sealed class ArchitectureTests
{
    /// <summary>
    /// <c>EasyFix.Core</c> no puede arrastrar WPF. Si la lógica de negocio empieza a depender de la
    /// UI, deja de ser testeable sin levantar una ventana — y el plan lo prohíbe explícitamente.
    /// </summary>
    [Fact]
    public void Core_NoReferenciaWpf()
    {
        var referenced = typeof(StartupClassifier).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        string[] forbidden = { "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml" };

        foreach (string name in forbidden)
        {
            Assert.DoesNotContain(name, referenced);
        }
    }
}
