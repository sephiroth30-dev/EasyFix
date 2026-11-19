using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Resolución de la URL del instalador cuando el origen no es GitHub ni una URL fija.
/// </summary>
/// <remarks>
/// <para><b>El fallo que originó esto.</b> «Adobe volvió a dar el error, dice que winget no está
/// disponible». Adobe Reader, VLC y AnyDesk eran los últimos tres paquetes que solo sabían
/// instalarse por winget: en un equipo donde winget falta o falla, el técnico se quedaba sin
/// ellos aunque los otros siete sí entraran.</para>
///
/// <para>Los tres necesitaban algo que no existía: Adobe escribe la versión dentro de la ruta del
/// archivo y VLC publica un listado de directorio. Clavar la versión en la configuración no era
/// opción — envejece sola y termina instalando un Reader con vulnerabilidades conocidas, o
/// devolviendo 404.</para>
/// </remarks>
public sealed class DirectDownloadResolveTests
{
    /// <summary>Descargador falso: anota qué URL se le pidió y devuelve el índice que se le dé.</summary>
    private sealed class RecordingDownloader : IFileDownloader
    {
        private readonly string? _indexText;

        public RecordingDownloader(string? indexText) => _indexText = indexText;

        public Uri? Requested { get; private set; }

        public Task<string> DownloadAsync(
            Uri url, string fileName, IProgress<string>? p, CancellationToken ct)
        {
            Requested = url;

            string path = Path.Combine(Path.GetTempPath(), $"ddr-{Guid.NewGuid()}-{fileName}");
            File.WriteAllText(path, "x");
            return Task.FromResult(path);
        }

        public Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct) =>
            throw new InvalidOperationException("Estos paquetes no vienen de GitHub.");

        public Task<string?> FetchTextAsync(Uri url, CancellationToken ct) =>
            Task.FromResult(_indexText);
    }

    private sealed class OkRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken ct = default,
            IReadOnlyCollection<int>? benignExitCodes = null) =>
            Task.FromResult(new ProcessResult(0, "", "", false, TimeSpan.Zero));
    }

    private static async Task<(WingetResult Result, Uri? Downloaded)> Install(
        DirectDownload direct, string? indexText)
    {
        var downloader = new RecordingDownloader(indexText);

        var installer = new DirectDownloadInstaller(
            downloader,
            new OkRunner(),
            new ThresholdOptions(),
            NullLogger<DirectDownloadInstaller>.Instance);

        var package = new WingetPackage { Id = "Test.Pkg", Name = "Programa", Direct = direct };

        WingetResult result = await installer.InstallAsync(package, null, CancellationToken.None);

        return (result, downloader.Requested);
    }

    [Fact]
    public async Task ConPlantilla_LaVersionLeidaDelOrigenEntraEnLaRutaSinLosPuntos()
    {
        // La forma de Adobe: la API dice «26.003.22110» pero la carpeta se llama «2600322110».
        var direct = new DirectDownload
        {
            IndexUrl = "https://origen.invalid/products",
            MatchPattern = "\"version\"\\s*:\\s*\"([0-9.]+)\"",
            UrlTemplate = "https://cdn.invalid/{vdigits}/Rdr{vdigits}.exe",
            DetectDisplayName = "Programa de prueba",
        };

        (WingetResult result, Uri? downloaded) = await Install(
            direct, """{"products":{"reader":[{"version":"26.003.22110"}]}}""");

        Assert.Equal(WingetOutcome.Installed, result.Outcome);
        Assert.Equal("https://cdn.invalid/2600322110/Rdr2600322110.exe", downloaded?.ToString());
    }

    [Fact]
    public async Task SinPlantilla_ElArchivoDelListadoSeResuelveContraElIndice()
    {
        // La forma de VLC: un listado de directorio donde el nombre del archivo trae la versión.
        var direct = new DirectDownload
        {
            IndexUrl = "https://origen.invalid/vlc/last/win64/",
            MatchPattern = "vlc-[0-9.]+-win64\\.msi(?![\\w.])",
            DetectDisplayName = "Programa de prueba",
        };

        (WingetResult result, Uri? downloaded) = await Install(
            direct,
            """<a href="vlc-3.0.24-win64.exe">exe</a><a href="vlc-3.0.24-win64.msi">msi</a>""");

        Assert.Equal(WingetOutcome.Installed, result.Outcome);
        Assert.Equal("https://origen.invalid/vlc/last/win64/vlc-3.0.24-win64.msi", downloaded?.ToString());
    }

    [Fact]
    public async Task ElPatronNoSeQuedaConLaFirmaNiElHash()
    {
        // En el listado real de VideoLAN, al lado de cada instalador hay un .msi.asc, .msi.md5,
        // .msi.sha1 y .msi.sha256. Bajarse uno de esos sería descargar 200 bytes de texto y
        // ejecutarlos como si fueran el instalador.
        var direct = new DirectDownload
        {
            IndexUrl = "https://origen.invalid/d/",
            MatchPattern = "vlc-[0-9.]+-win64\\.msi(?![\\w.])",
            DetectDisplayName = "Programa de prueba",
        };

        (_, Uri? downloaded) = await Install(
            direct, "vlc-3.0.24-win64.msi.asc vlc-3.0.24-win64.msi.sha256 vlc-3.0.24-win64.msi");

        Assert.EndsWith("vlc-3.0.24-win64.msi", downloaded?.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(".asc", downloaded?.ToString() ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(".sha", downloaded?.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiElOrigenCambioDeFormato_NoDescargaNadaYLoDice()
    {
        // Falla cerrado. Si el patrón deja de coincidir —porque el origen cambió— lo peor que
        // puede pasar es que EasyFix invente una URL y baje cualquier cosa.
        var direct = new DirectDownload
        {
            IndexUrl = "https://origen.invalid/products",
            MatchPattern = "\"version\"\\s*:\\s*\"([0-9.]+)\"",
            UrlTemplate = "https://cdn.invalid/{vdigits}/Rdr{vdigits}.exe",
            DetectDisplayName = "Programa de prueba",
        };

        (WingetResult result, Uri? downloaded) = await Install(direct, "<html>ya no hay JSON acá</html>");

        Assert.Equal(WingetOutcome.NotFound, result.Outcome);
        Assert.Null(downloaded);
        Assert.Contains("appsettings.json", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiElOrigenNoContesta_NoDescargaNada()
    {
        var direct = new DirectDownload
        {
            IndexUrl = "https://origen.invalid/products",
            MatchPattern = "\"version\"\\s*:\\s*\"([0-9.]+)\"",
            UrlTemplate = "https://cdn.invalid/{v}/x.exe",
            DetectDisplayName = "Programa de prueba",
        };

        (WingetResult result, Uri? downloaded) = await Install(direct, null);

        Assert.Equal(WingetOutcome.NotFound, result.Outcome);
        Assert.Null(downloaded);
    }

    // ---- Configuración real -------------------------------------------------------------------

    [Fact]
    public void NingunProgramaDelCatalogoDependeDeWinget()
    {
        // Es el pedido entero: «no instaló nada porque falla el winget». Mientras un paquete no
        // tenga 'direct', en un equipo sin winget ese programa no se instala — y el técnico se
        // entera recién en la casa del cliente.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        var dependientes = options.WingetPackages
            .Where(p => p.Direct is null)
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            dependientes.Count == 0,
            $"Estos programas solo se pueden instalar por winget: {string.Join(", ", dependientes)}. " +
            "Buscá su origen oficial y configurale 'direct' antes de publicar.");
    }

    [Fact]
    public void TodoOrigenConIndiceTieneSuPatron()
    {
        // 'indexUrl' sin 'matchPattern' no es un error de compilación ni de arranque: la
        // resolución simplemente se saltea esa rama y cae a 'url', que en estos paquetes no
        // existe. El resultado sería «no se encontró un instalador» sin ninguna pista del motivo.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.Direct?.IndexUrl is not null))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(package.Direct!.MatchPattern),
                $"{package.Name} tiene 'indexUrl' pero no 'matchPattern'.");
        }
    }

    [Fact]
    public void TodosLosOrigenesSonHttps()
    {
        // De estas URLs sale un ejecutable que se corre con permisos de administrador en el
        // equipo de un cliente. Por HTTP sin cifrar, cualquiera en el camino elige qué se corre.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.Direct is not null))
        {
            foreach (string? candidate in new[]
                     {
                         package.Direct!.Url,
                         package.Direct.IndexUrl,
                         package.Direct.UrlTemplate,
                     })
            {
                if (candidate is { Length: > 0 })
                {
                    Assert.StartsWith("https://", candidate, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void LasPlantillasUsanUnTokenQueExiste()
    {
        // Un «{version}» en vez de «{v}» no lo detecta nadie: la plantilla se usa tal cual y
        // EasyFix pide una URL con llaves adentro, que devuelve 404 en el equipo del cliente.
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        foreach (WingetPackage package in options.WingetPackages.Where(p => p.Direct?.UrlTemplate is not null))
        {
            string resolved = package.Direct!.UrlTemplate!
                .Replace("{vdigits}", "0", StringComparison.Ordinal)
                .Replace("{v}", "0", StringComparison.Ordinal);

            Assert.False(
                resolved.Contains('{', StringComparison.Ordinal),
                $"La plantilla de {package.Name} tiene un token que nadie reemplaza: " +
                $"{package.Direct.UrlTemplate}. Los válidos son {{v}} y {{vdigits}}.");
        }
    }
}
