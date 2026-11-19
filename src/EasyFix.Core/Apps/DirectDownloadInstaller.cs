using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Configuration;
using EasyFix.Core.Network;
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

    /// <summary>
    /// Trae el contenido de una URL como texto, o <c>null</c> si no se pudo.
    /// </summary>
    /// <remarks>
    /// Para los orígenes que no son GitHub y publican la última versión en un listado de
    /// directorio o en una respuesta JSON. Devuelve el texto crudo a propósito: quién interpreta
    /// ese texto es <see cref="DirectDownloadInstaller"/>, que así se puede probar sin red.
    /// </remarks>
    Task<string?> FetchTextAsync(Uri url, CancellationToken ct);
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
    private readonly IInstalledProgramsReader? _installedPrograms;
    private readonly ILogger<DirectDownloadInstaller> _logger;

    /// <param name="installedPrograms">
    /// Para no reinstalar lo que ya está. <c>null</c> desactiva la comprobación — en ese caso se
    /// instala siempre, que es el comportamiento viejo.
    /// </param>
    public DirectDownloadInstaller(
        IFileDownloader downloader,
        IProcessRunner runner,
        ThresholdOptions thresholds,
        ILogger<DirectDownloadInstaller> logger,
        IInstalledProgramsReader? installedPrograms = null)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        _downloader = downloader;
        _runner = runner;
        _thresholds = thresholds;
        _installedPrograms = installedPrograms;
        _logger = logger;
    }

    /// <summary>
    /// <c>true</c> si el programa ya figura en las claves <c>Uninstall</c> del equipo.
    /// </summary>
    /// <remarks>
    /// <para><b>El bug que esto arregla.</b> «Ya estaba instalado» lo detectaba solo el camino de
    /// winget, leyendo su salida. Los paquetes de descarga directa —Chrome y RustDesk— no pasan
    /// por ahí: bajaban el instalador y lo corrían <i>siempre</i>, incluso en un equipo que ya los
    /// tenía. En un equipo de cliente eso son varios minutos y varios MB tirados, y encima el
    /// resumen decía «instalado» como si hubiera hecho falta.</para>
    ///
    /// <para>Falla hacia instalar: si la lista no se puede leer, se instala igual. Reinstalar
    /// cuesta tiempo; saltear por error dejaría al cliente sin el programa.</para>
    /// </remarks>
    private bool IsAlreadyInstalled(WingetPackage package)
    {
        if (_installedPrograms is null)
        {
            return false;
        }

        // El nombre a buscar es configurable porque no siempre coincide con el del catálogo: el
        // registro puede decir «Google Chrome» para un paquete llamado de otra forma.
        string needle = package.Direct?.DetectDisplayName ?? package.Name;

        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        try
        {
            return _installedPrograms.Read().Any(p =>
                p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "No se pudo comprobar si {Package} ya estaba instalado; se intenta instalar.",
                package.Id);
            return false;
        }
    }

    /// <summary>
    /// Descarga e instala en silencio. Devuelve un <see cref="WingetResult"/> para que el reporte
    /// trate igual a los dos caminos de instalación.
    /// </summary>
    /// <param name="onInstallerDownloaded">
    /// Se invoca con la ruta del instalador ya en disco, antes de ejecutarlo. La interfaz lo usa
    /// para sacarle el icono real del programa — el archivo está ahí justo cuando hace falta
    /// mostrarlo, y es la única fuente del logo auténtico que no implica empaquetar binarios de
    /// marcas ajenas ni bajar nada extra.
    /// </param>
    public async Task<WingetResult> InstallAsync(
        WingetPackage package,
        IProgress<string>? progress,
        CancellationToken ct,
        Action<string>? onInstallerDownloaded = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (package.Direct is not { } direct)
        {
            return new WingetResult(package.Id, WingetOutcome.Failed,
                $"{package.Name} no tiene configurada una descarga directa.", null);
        }

        // ANTES de bajar nada: si ya está, no se toca. Descargar e instalar encima de una
        // instalación buena es tiempo del técnico y ancho de banda del cliente, por nada.
        if (IsAlreadyInstalled(package))
        {
            _logger.LogInformation("{Package} ya estaba instalado; no se descarga.", package.Id);

            return new WingetResult(package.Id, WingetOutcome.AlreadyInstalled,
                $"{package.Name} ya estaba instalado en este equipo, así que no se volvió a " +
                "descargar. Para pasarlo a la última versión, usá «Actualizar lo instalado».",
                null);
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

            if (AsNetworkFailure(package, ex) is { } offline)
            {
                return offline;
            }

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

            if (AsNetworkFailure(package, ex) is { } offline)
            {
                return offline;
            }

            return new WingetResult(package.Id, WingetOutcome.Failed,
                $"No se pudo descargar {package.Name}: {ex.Message}", null);
        }

        // El icono sale del instalador descargado, antes de correrlo: después se borra.
        try
        {
            onInstallerDownloaded?.Invoke(installerPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer el icono de {Package}.", package.Id);
        }

        try
        {
            progress?.Report($"Instalando {package.Name}…");

            // Timeout propio del paquete, mucho menor que el general de 30 min: ese está pensado
            // para DISM. Un instalador colgado bloquea toda la tanda.
            TimeSpan timeout = TimeSpan.FromMinutes(Math.Max(1, direct.TimeoutMinutes));

            (string executable, IReadOnlyList<string> arguments) = BuildInvocation(installerPath, direct);

            ProcessResult result = await _runner
                .RunAsync(executable, arguments, timeout, ct)
                .ConfigureAwait(false);

            if (result.TimedOut)
            {
                return new WingetResult(package.Id, WingetOutcome.TimedOut,
                    $"El instalador de {package.Name} no terminó en {timeout.TotalMinutes:0} minutos y " +
                    "se canceló. Puede haber abierto una ventana esperando confirmación, o el " +
                    "argumento de instalación silenciosa no ser el correcto para esta versión.", null);
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

    /// <summary>
    /// Devuelve un resultado de «sin internet» si la excepción es de red, o <c>null</c> si no lo es.
    /// </summary>
    /// <remarks>
    /// El caso del log del 2026-08-29: <c>SocketException 11001</c> resolviendo <c>dl.google.com</c>
    /// se reportaba como «No se pudo descargar Google Chrome: Host desconocido. (dl.google.com:443)».
    /// Es cierto, pero le deja al técnico el trabajo de saber que «host desconocido» quiere decir
    /// «este equipo no tiene internet» — y en ese equipo nadie lo hizo: el diagnóstico siguió como si
    /// el problema fuera de winget.
    /// </remarks>
    private WingetResult? AsNetworkFailure(WingetPackage package, Exception exception)
    {
        if (NetworkDiagnosis.FindSocketException(exception) is null)
        {
            return null;
        }

        ConnectivityResult diagnosis = NetworkDiagnosis.Classify(exception);

        _logger.LogWarning(
            "{Package} no se pudo descargar por falta de red ({Status}). {Technical}",
            package.Id, diagnosis.Status, diagnosis.TechnicalDetail);

        return WingetResultParser.Offline(package.Id, diagnosis);
    }

    private async Task<Uri?> ResolveUrlAsync(
        DirectDownload direct, IProgress<string>? progress, CancellationToken ct)
    {
        if (direct.GitHubRepository is { Length: > 0 } repo)
        {
            progress?.Report($"Buscando la última versión en GitHub ({repo})…");

            // El patrón preferido se intenta primero: sirve para elegir el MSI cuando existe, que se
            // instala en silencio de forma más confiable que un EXE.
            if (direct.PreferredAssetPattern is { Length: > 0 } preferred)
            {
                Uri? better = await _downloader
                    .ResolveGitHubLatestAsync(repo, preferred, ct)
                    .ConfigureAwait(false);

                if (better is not null)
                {
                    _logger.LogInformation(
                        "{Repo}: se usa el asset preferido que coincide con '{Pattern}'.", repo, preferred);
                    return better;
                }

                _logger.LogInformation(
                    "{Repo}: no hay asset que coincida con '{Pattern}'; se cae al patrón normal.",
                    repo, preferred);
            }

            return await _downloader
                .ResolveGitHubLatestAsync(repo, direct.AssetPattern ?? ".exe", ct)
                .ConfigureAwait(false);
        }

        if (direct.IndexUrl is { Length: > 0 } index &&
            direct.MatchPattern is { Length: > 0 } pattern &&
            Uri.TryCreate(index, UriKind.Absolute, out Uri? indexUri))
        {
            return await ResolveFromIndexAsync(direct, indexUri, pattern, progress, ct)
                .ConfigureAwait(false);
        }

        return Uri.TryCreate(direct.Url, UriKind.Absolute, out Uri? parsed) ? parsed : null;
    }

    /// <summary>
    /// Lee la última versión del origen oficial y arma la URL del instalador.
    /// </summary>
    /// <remarks>
    /// <para><b>Qué fallo arregla.</b> Adobe Reader, VLC y AnyDesk eran los últimos tres que solo
    /// se podían instalar por winget, y en un equipo sin winget el técnico veía «winget no está
    /// disponible» y se quedaba sin el programa. Ninguno de los tres se podía resolver con lo que
    /// ya había: no están en GitHub, y sus URLs llevan la versión adentro.</para>
    ///
    /// <para><b>Por qué se resuelve en el momento y no se clava la versión.</b> Una URL con la
    /// versión escrita en la configuración funciona el día que se escribe y después se pudre sola:
    /// Adobe publica una versión por mes, y la vieja termina devolviendo 404 — o peor, sigue
    /// funcionando e instala un Reader con vulnerabilidades conocidas.</para>
    /// </remarks>
    private async Task<Uri?> ResolveFromIndexAsync(
        DirectDownload direct,
        Uri index,
        string pattern,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"Buscando la última versión en {index.Host}…");

        string? text = await _downloader.FetchTextAsync(index, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("{Index} no devolvió contenido donde buscar la versión.", index);
            return null;
        }

        Match match;
        try
        {
            // El timeout es por las dudas: los patrones salen de nuestro appsettings.json, pero un
            // patrón mal escrito colgaría la instalación entera sin dar ninguna pista.
            match = Regex.Match(
                text, pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            _logger.LogError(ex, "El patrón '{Pattern}' no se pudo aplicar a {Index}.", pattern, index);
            return null;
        }

        if (!match.Success)
        {
            _logger.LogWarning(
                "En {Index} no hay nada que coincida con '{Pattern}'. Puede haber cambiado el " +
                "formato del origen: revisá 'matchPattern' en appsettings.json.", index, pattern);
            return null;
        }

        string captured = match.Groups.Count > 1 && match.Groups[1].Success
            ? match.Groups[1].Value
            : match.Value;

        if (string.IsNullOrWhiteSpace(captured))
        {
            return null;
        }

        if (direct.UrlTemplate is { Length: > 0 } template)
        {
            string built = template
                .Replace("{vdigits}", new string(captured.Where(char.IsDigit).ToArray()), StringComparison.Ordinal)
                .Replace("{v}", captured, StringComparison.Ordinal);

            _logger.LogInformation("{Index}: versión {Version}; se baja {Url}.", index, captured, built);

            return Uri.TryCreate(built, UriKind.Absolute, out Uri? templated) ? templated : null;
        }

        // Sin plantilla, lo capturado es el nombre de archivo dentro del listado.
        _logger.LogInformation("{Index}: se eligió el archivo {File}.", index, captured);

        return Uri.TryCreate(index, captured, out Uri? relative) ? relative : null;
    }

    /// <summary>
    /// Nombre de archivo seguro. El nombre viene de la URL, así que se sanea: nunca se construye una
    /// ruta con texto de la red sin filtrarlo.
    /// </summary>
    /// <summary>
    /// Decide qué ejecutar: el instalador directamente, o msiexec cuando el asset es un MSI.
    /// </summary>
    /// <remarks>
    /// Un MSI no es ejecutable: hay que lanzarlo con <c>msiexec /i &lt;archivo&gt; /qn</c>. Se prefiere
    /// cuando existe porque su instalación silenciosa es determinista, a diferencia de los EXE que
    /// dependen del empaquetador que usó el fabricante.
    /// </remarks>
    private static (string Executable, IReadOnlyList<string> Arguments) BuildInvocation(
        string installerPath, DirectDownload direct)
    {
        if (!DirectDownload.IsMsi(installerPath))
        {
            // AnyDesk necesita que se le diga la carpeta de destino, y escribir «C:\Program Files
            // (x86)» a mano rompe en Windows en otro idioma o instalado en otra unidad.
            IReadOnlyList<string> args = (direct.SilentArgs ?? Array.Empty<string>())
                .Select(Environment.ExpandEnvironmentVariables)
                .ToList();

            return (installerPath, args);
        }

        var arguments = new List<string> { "/i", installerPath, "/qn", "/norestart" };

        // SilentArgs NO se agregan acá, y eso arregla un fallo real.
        //
        // Son los modificadores del instalador .EXE de cada programa (RustDesk usa
        // «--silent-install»). msiexec no los entiende: con un argumento inválido devuelve 1639
        // (0x667, ERROR_INVALID_COMMAND_LINE) y, peor, ABRE SU VENTANA DE AYUDA — lo que en un
        // equipo de cliente aparece como «el instalador abrió una ventana rara y falló».
        //
        // RustDesk prefiere el .msi (preferredAssetPattern) pero declara los args del .exe como
        // respaldo, así que caía justo en este caso. Para un MSI, «/qn /norestart» YA es todo el
        // modo silencioso que hace falta.
        //
        // Si alguna vez hace falta pasarle propiedades a un MSI (del estilo «PROP=valor»), va un
        // campo nuevo y explícito en la configuración, no este.

        return (
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            arguments);
    }

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

        // 40 minutos, no 10. El límite de HttpClient cubre TODA la operación, incluido leer el
        // cuerpo: con 10 minutos, el instalador de Adobe Reader —que hoy pesa unos 800 MB— se
        // cortaba a la mitad en cualquier conexión por debajo de ~11 Mbps, y el técnico veía una
        // cancelación sin motivo aparente. El botón de cancelar sigue funcionando, y la descarga
        // informa los MB cada segundo, así que una conexión muerta se nota mucho antes.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(40) };

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

        // El chequeo de arriba solo mira la URL de ENTRADA. HttpClient sigue redirects solo
        // (ej. el aka.ms de VCLibs), y si alguna vez un salto intermedio bajara a HTTP —un proxy
        // corporativo interceptando, o un cambio de configuración del lado del servidor— el
        // esquema de entrada ya no lo detecta. RequestMessage.RequestUri refleja la ÚLTIMA
        // petición de la cadena de redirects, así que es el que hay que mirar.
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" } finalUri)
        {
            throw new InvalidOperationException(
                $"La descarga terminó en '{finalUri}', que no es HTTPS. Se corta antes de guardar nada.");
        }

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        // Progreso REAL, por bytes. Antes se reportaba una sola vez el tamaño y después silencio
        // hasta terminar: en una conexión lenta eso son minutos sin ninguna señal, y el técnico no
        // puede distinguir «bajando» de «colgado». Es exactamente lo que se reportó con el
        // bootstrap de winget, que tardó más de 10 minutos sin decir nada.
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long received = 0;
            DateTimeOffset lastReport = DateTimeOffset.MinValue;

            while (true)
            {
                int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                // Una vez por segundo: más seguido satura el hilo de la interfaz sin aportar nada
                // que el ojo pueda seguir.
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - lastReport < TimeSpan.FromSeconds(1))
                {
                    continue;
                }

                lastReport = now;

                // El prefijo «[nn]» lo consume la interfaz para mover la barrita de esta fila.
                // Es un canal de texto y agregar un tipo nuevo solo para esto no se justifica;
                // si el prefijo no está, la fila simplemente queda indeterminada.
                progress?.Report(total is long size && size > 0
                    ? $"[{100.0 * received / size:0}] {received / 1024 / 1024} MB de {size / 1024 / 1024} MB"
                    : $"{received / 1024 / 1024} MB descargados");
            }
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

    public async Task<string?> FetchTextAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            // Lo que se lee de acá decide qué ejecutable se baja y se corre. Por HTTP sin cifrar
            // cualquiera en el camino podría apuntarlo a otro lado.
            throw new InvalidOperationException(
                $"Solo se consultan orígenes por HTTPS. La URL '{url}' usa {url.Scheme}.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // El Accept de la instancia dice «application/vnd.github+json», que es lo que quiere la API
        // de releases. Acá se consultan listados HTML y JSON de otros orígenes: dejarlo puesto hace
        // que alguno conteste 406 y la instalación falle por una cabecera, no por la red.
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/json, text/html, */*");

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "{Url} respondió {Status} al buscar la última versión.", url, (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
