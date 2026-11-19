using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Drivers;

/// <summary>
/// Consulta e instala actualizaciones de driver vía el Agente de Windows Update (WUA) — el mismo
/// motor que usa «Buscar actualizaciones» y la búsqueda automática del Administrador de
/// dispositivos.
/// </summary>
/// <remarks>
/// <para><b>Por qué WUA y no un driver update de terceros.</b> El catálogo de WUA ya validó y
/// firmó cada driver para la combinación exacta de hardware y build de Windows de ESTE equipo —
/// es la misma vía que usaría el propio Windows si se dejara. Los «actualizadores de drivers»
/// (Driver Booster, DriverPack...) están en <c>appsettings.json</c> como PUP justamente porque
/// adivinan en vez de usar esta fuente. No se construyó acá un equivalente de eso.</para>
///
/// <para><b>COM tardío (<c>dynamic</c>), sin agregar ningún NuGet.</b> <c>Microsoft.Update.Session</c>
/// es un componente de Windows desde Vista (<c>wuapi.dll</c>), con contrato estable y
/// extensamente documentado — es lo mismo que usa el módulo de PowerShell <c>PSWindowsUpdate</c> por
/// debajo. Se accede por <c>Type.GetTypeFromProgID</c> + <c>dynamic</c>, el mismo mecanismo con el
/// que <c>System.Management</c> ya habla WMI en <c>SystemProbe</c>, sin sumar superficie de
/// dependencias nueva.</para>
///
/// <para><b>Lo que esto significa en términos de riesgo: es el código menos verificable de todo
/// el proyecto.</b> Con <c>dynamic</c> el compilador NO revisa que <c>CreateUpdateSearcher</c>,
/// <c>ServerSelection</c> o cualquier otro miembro exista — eso solo se sabe en tiempo de
/// ejecución, en un Windows real. Todo lo demás de EasyFix que se compila limpio ya validó algo
/// (nombres de propiedad, tipos); esto no. Cada llamada está en su propio <c>try/catch</c> ancho,
/// mismo principio que <c>SystemProbe</c>: una sonda que falla, falla sola y se reporta como «no
/// se pudo determinar», nunca como «no hay nada».</para>
///
/// <para><b>Catálogo público, no WSUS corporativo.</b> <c>ServerSelection = 2</c> (<c>ssWindowsUpdate</c>)
/// fuerza el catálogo público de Microsoft. Sin esto, un equipo de dominio con WSUS configurado
/// buscaría contra el servidor corporativo, que puede no tener drivers en su catálogo y tardar
/// minutos en darse por vencido.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateDriverCatalog : IDriverCatalog
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string SearchCriteria = "IsInstalled=0 and Type='Driver'";

    /// <summary>ssWindowsUpdate: el catálogo público, no el WSUS que el equipo tenga configurado.</summary>
    private const int ServerSelectionWindowsUpdate = 2;

    /// <summary>orcSucceeded del enum <c>OperationResultCode</c> de WUA.</summary>
    private const int OperationResultSucceeded = 2;

    private readonly ILogger<WindowsUpdateDriverCatalog> _logger;

    public WindowsUpdateDriverCatalog(ILogger<WindowsUpdateDriverCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<DriverSearchResult> SearchAsync(CancellationToken ct) =>
        Task.Run(() => Search(ct), ct);

    public Task<DriverInstallResult> InstallAsync(
        IReadOnlyList<string> updateIds, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        return Task.Run(() => Install(updateIds, progress, ct), ct);
    }

    private DriverSearchResult Search(CancellationToken ct)
    {
        Type? sessionType = Type.GetTypeFromProgID(SessionProgId);
        if (sessionType is null)
        {
            // No debería pasar en ningún Windows soportado; si pasa, es mejor saberlo que
            // reventar más abajo con un mensaje críptico de COM.
            return DriverSearchResult.Failed(
                $"'{SessionProgId}' no está registrado en este equipo.");
        }

        try
        {
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            dynamic searchResult = searcher.Search(SearchCriteria);

            var updates = new List<DriverUpdate>();

            foreach (dynamic update in searchResult.Updates)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    updates.Add(ToDriverUpdate(update));
                }
                catch (Exception ex)
                {
                    // Una entrada que no se pudo leer no puede tumbar el resto de la búsqueda.
                    _logger.LogWarning(ex, "No se pudo leer una actualización de driver del catálogo.");
                }
            }

            _logger.LogInformation(
                "Catálogo de drivers: {Count} actualización(es) disponibles.", updates.Count);

            return DriverSearchResult.Ok(updates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cualquier cosa de COM (servicio de Windows Update detenido, política de grupo que
            // bloquea la búsqueda, el miembro que no existía) cae acá. Sin inventar "cero
            // actualizaciones": se dice que no se pudo buscar.
            _logger.LogWarning(ex, "No se pudo consultar el catálogo de drivers de Windows Update.");
            return DriverSearchResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static DriverUpdate ToDriverUpdate(dynamic update)
    {
        string title = (string)update.Title;
        string? description = update.Description as string;
        string updateId = (string)update.Identity.UpdateID;

        var categories = new List<string>();
        foreach (dynamic category in update.Categories)
        {
            if (category.Name is string name)
            {
                categories.Add(name);
            }
        }

        return new DriverUpdate(updateId, title, description,
            DriverRiskClassifier.Classify(title, categories));
    }

    private DriverInstallResult Install(
        IReadOnlyList<string> updateIds, IProgress<string>? progress, CancellationToken ct)
    {
        if (updateIds.Count == 0)
        {
            return new DriverInstallResult(true, false, "No había nada para instalar.");
        }

        Type? sessionType = Type.GetTypeFromProgID(SessionProgId);
        if (sessionType is null)
        {
            return new DriverInstallResult(false, false,
                $"'{SessionProgId}' no está registrado en este equipo.");
        }

        try
        {
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            progress?.Report("Volviendo a buscar los drivers a instalar…");
            dynamic searchResult = searcher.Search(SearchCriteria);

            dynamic toInstall = Activator.CreateInstance(
                Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;

            var matchedTitles = new List<string>();

            foreach (dynamic update in searchResult.Updates)
            {
                ct.ThrowIfCancellationRequested();

                string id = (string)update.Identity.UpdateID;
                if (!updateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // EasyFix ya decidió qué instalar ANTES de llegar acá (solo lo clasificado como
                // Safe). Aceptar el EULA es parte de instalar algo que ya se decidió instalar, no
                // una segunda confirmación independiente.
                update.AcceptEula();
                toInstall.Add(update);
                matchedTitles.Add((string)update.Title);
            }

            if (toInstall.Count == 0)
            {
                return new DriverInstallResult(false, false,
                    "Los drivers pedidos ya no están en el catálogo (pueden haberse instalado " +
                    "en otra corrida, o Windows Update los retiró).");
            }

            progress?.Report($"Descargando {toInstall.Count} driver(s): {string.Join(", ", matchedTitles)}");

            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = toInstall;
            dynamic downloadResult = downloader.Download();

            if ((int)downloadResult.ResultCode != OperationResultSucceeded)
            {
                return new DriverInstallResult(false, false,
                    $"La descarga no terminó bien (código {(int)downloadResult.ResultCode}).");
            }

            progress?.Report("Instalando…");

            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = toInstall;
            dynamic installResult = installer.Install();

            bool succeeded = (int)installResult.ResultCode == OperationResultSucceeded;
            bool rebootRequired = (bool)installResult.RebootRequired;

            string detail = succeeded
                ? $"Instalado: {string.Join(", ", matchedTitles)}."
                : $"La instalación no terminó bien (código {(int)installResult.ResultCode}).";

            _logger.LogInformation(
                "Instalación de drivers: éxito={Success}, reinicio={Reboot}. {Titles}",
                succeeded, rebootRequired, string.Join(", ", matchedTitles));

            return new DriverInstallResult(succeeded, rebootRequired, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló la instalación de drivers.");
            return new DriverInstallResult(false, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
