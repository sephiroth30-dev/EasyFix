using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Apps;

/// <summary>Descarga un archivo. Se abstrae para poder testear el instalador sin red.</summary>
public interface IFileDownloader
{
    /// <summary>Descarga a un archivo temporal y devuelve su ruta.</summary>
    Task<string> DownloadAsync(Uri url, string fileName, IProgress<string>? progress, CancellationToken ct);

    /// <summary>
    /// Resuelve la URL del último release de un repositorio de GitHub.
    /// </summary>
    /// <param name="repository">Formato <c>owner/repo</c>.</param>
    /// <param name="assetPattern">
    /// Fragmento que tiene que contener el nombre del asset, p. ej. <c>x86_64.exe</c>.
    /// </param>
    Task<Uri?> ResolveGitHubLatestAsync(string repository, string assetPattern, CancellationToken ct);
}

/// <summary>
/// Instala programas que no están en el repositorio de winget, bajándolos de su origen oficial.
/// </summary>
/// <remarks>
/// <para><b>Por qué hace falta.</b> No todo está en winget, y lo que está puede salir: RustDesk fue
/// removido del repositorio en 2026 porque un antivirus lo marcó como <c>RemoteAdmin.RustDesk</c> —un
/// falso positivo— y el ID dejó de resolver. Depender solo de winget significa que un paquete
/// desaparece del catálogo y la herramienta deja de poder instalarlo.</para>
///
/// <para><b>Siempre la última versión.</b> Para los que vienen de GitHub se consulta la API de
/// releases y se toma el más reciente, así que no hay ninguna versión clavada que envejezca.</para>
///
/// <para><b>Nada se empaqueta en el .exe.</b> Igual que con winget: se descarga en el momento.</para>
/// </remarks>
public sealed class DirectDownloadInstaller
{
    private readonly IFileDownloader _downloader;
    private readonly IProcessRunner _runner;
    private readonly ThresholdOptions _thresholds;
    private readonly ILogger<DirectDownloadInstaller> _logger;

    public DirectDownloadInstaller(
        IFileDownloader downloader,
        IProcessRunner runner,
        ThresholdOptions thresholds,
        ILogger<DirectDownloadInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        _downloader = downloader;
        _runner = runner;
        _thresholds = thresholds;
        _logger = logger;
    }

    /// <summary>
    /// Descarga e instala en silencio. Devuelve un <see cref="WingetResult"/> para que el reporte
    /// trate igual a los dos caminos de instalación.
    /// </summary>
    public async Task<WingetResult> InstallAsync(
        WingetPackage package,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Direct is not { } direct)
        {
            return new WingetResult(package.Id, WingetOutcome.Failed,
                $"{package.Name} no tiene configurada una descarga directa.", null);
        }

        Uri? url;
        try
        {
            url = await ResolveUrlAsync(direct, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo resolver la URL de {Package}.", package.Id);
            return new WingetResult(package.Id, WingetOutcome.Failed,
                $"No se pudo averiguar de dónde bajar {package.Name}: {ex.Message}", null);
        }

        if (url is null)
        {
            return new WingetResult(package.Id, WingetOutcome.NotFound,
                $"No se encontró un instalador para {package.Name} en su origen oficial. " +
                $"Puede haber cambiado el nombre del archivo: revisá «assetPattern» en appsettings.json.",
                null);
        }

        string installerPath;
        try
        {
            progress?.Report($"Descargando {package.Name} desde {url.Host}…");
            installerPath = await _downloader
                .DownloadAsync(url, SafeFileName(package.Id, url), progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la descarga de {Package}.", package.Id);
            return new WingetResult(package.Id, WingetOutcome.Failed,
                $"No se pudo descargar {package.Name}: {ex.Message}", null);
        }

        try
        {
            progress?.Report($"Instalando {package.Name}…");

            ProcessResult result = await _runner
                .RunAsync(installerPath, direct.SilentArgs ?? Array.Empty<string>(),
                    _thresholds.ExternalProcessTimeout, ct)
                .ConfigureAwait(false);

            if (result.TimedOut)
            {
                return new WingetResult(package.Id, WingetOutcome.TimedOut,
                    $"El instalador de {package.Name} excedió el tiempo límite.", null);
            }

            return result.Succeeded
                ? new WingetResult(package.Id, WingetOutcome.Installed,
                    $"{package.Name} se instaló desde su origen oficial (última versión).", result.ExitCode)
                : new WingetResult(package.Id, WingetOutcome.Failed,
                    $"El instalador de {package.Name} devolvió el código 0x{result.ExitCode:X8}. " +
                    FirstLine(result.StandardError), result.ExitCode);
        }
        finally
        {
            // El instalador descargado no tiene por qué quedar en el disco del cliente.
            TryDelete(installerPath);
        }
    }

    private async Task<Uri?> ResolveUrlAsync(
        DirectDownload direct, IProgress<string>? progress, CancellationToken ct)
    {
        if (direct.GitHubRepository is { Length: > 0 } repo)
        {
            progress?.Report($"Buscando la última versión en GitHub ({repo})…");
            return await _downloader
                .ResolveGitHubLatestAsync(repo, direct.AssetPattern ?? ".exe", ct)
                .ConfigureAwait(false);
        }

        return Uri.TryCreate(direct.Url, UriKind.Absolute, out Uri? parsed) ? parsed : null;
    }

    /// <summary>
    /// Nombre de archivo seguro. El nombre viene de la URL, así que se sanea: nunca se construye una
    /// ruta con texto de la red sin filtrarlo.
    /// </summary>
    private static string SafeFileName(string packageId, Uri url)
    {
        string fromUrl = Path.GetFileName(url.LocalPath);
        string extension = Path.GetExtension(fromUrl);

        if (extension is not (".exe" or ".msi" or ".msix" or ".msixbundle"))
        {
            extension = ".exe";
        }

        string safeId = string.Concat(packageId.Where(char.IsLetterOrDigit));
        return $"easyfix-{safeId}{extension}";
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudo borrar el instalador temporal {Path}.", path);
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Descarga por HTTP y resuelve releases de GitHub.
/// </summary>
public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpFileDownloader> _logger;

    public HttpFileDownloader(ILogger<HttpFileDownloader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub rechaza las peticiones sin User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EasyFix/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> DownloadAsync(
        Uri url, string fileName, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            // Descargar un ejecutable por HTTP sin cifrar permite que se lo cambien en el camino.
            throw new InvalidOperationException(
                $"Solo se descarga por HTTPS. La URL '{url}' usa {url.Scheme}.");
        }

        string destination = Path.Combine(Path.GetTempPath(), fileName);

        using HttpResponseMessage response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        if (total is long bytes)
        {
            progress?.Report($"Descargando {bytes / 1024 / 1024} MB…");
        }

        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Descargado {Url} en {Path}.", url, destination);
        return destination;
    }

    public async Task<Uri?> ResolveGitHubLatestAsync(
        string repository, string assetPattern, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPattern);

        var api = new Uri($"https://api.github.com/repos/{repository}/releases/latest");

        GitHubRelease? release = await _http
            .GetFromJsonAsync<GitHubRelease>(api, ct)
            .ConfigureAwait(false);

        if (release?.Assets is null || release.Assets.Count == 0)
        {
            _logger.LogWarning("El último release de {Repo} no trae assets.", repository);
            return null;
        }

        GitHubAsset? match = release.Assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Contains(assetPattern, StringComparison.OrdinalIgnoreCase));

        if (match?.DownloadUrl is null)
        {
            _logger.LogWarning(
                "Ningún asset de {Repo} {Tag} contiene '{Pattern}'. Disponibles: {Assets}.",
                repository, release.TagName, assetPattern,
                string.Join(", ", release.Assets.Select(a => a.Name)));
            return null;
        }

        _logger.LogInformation(
            "{Repo}: última versión {Tag}, asset {Asset}.", repository, release.TagName, match.Name);

        return Uri.TryCreate(match.DownloadUrl, UriKind.Absolute, out Uri? url) ? url : null;
    }

    public void Dispose() => _http.Dispose();

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("assets")]
        public IReadOnlyList<GitHubAsset>? Assets { get; init; }
    }

    private sealed record GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }
    }
}
