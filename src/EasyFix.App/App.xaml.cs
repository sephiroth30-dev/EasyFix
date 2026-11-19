// Los ImplicitUsings de un proyecto WPF no incluyen System.IO (el set de WindowsDesktop es distinto
// al de una librería), así que va explícito.
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EasyFix.App.ViewModels;
using EasyFix.App.Views;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Classification;
using EasyFix.Core.Cleaning;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Drivers;
using EasyFix.Core.Fixes;
using EasyFix.Core.Network;
using EasyFix.Core.Processes;
using EasyFix.Core.Recommendations;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace EasyFix.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Serilog.Core.Logger? _serilog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Cualquier excepción no manejada en el hilo de UI: se registra y se muestra, nunca se come
        // en silencio. En el equipo de un cliente, una app que desaparece sin decir nada es lo peor.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ApplySystemTheme();

        _serilog = BuildLogger();
        _services = BuildServices(_serilog);

        _services.GetRequiredService<ILogger<App>>()
            .LogInformation("EasyFix iniciado. Journals en {Dir}.", FileJournalSink.DefaultDirectory);

        _services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>
    /// Carga la paleta clara u oscura según lo que tenga configurado Windows.
    /// </summary>
    /// <remarks>
    /// <para>Se hace ANTES de construir la ventana: cambiar la paleta con la UI ya creada obliga a
    /// refrescar bindings y es una fuente de parpadeos. El técnico abre la app en el equipo del
    /// cliente, se adapta una vez y listo.</para>
    ///
    /// <para><b>El default es oscuro y eso es a propósito.</b> La paleta oscura es la que se vio
    /// funcionando en equipos reales; la clara es nueva y no se pudo ver renderizada. Si la lectura
    /// del registro falla por cualquier motivo, se queda la que ya está cargada desde
    /// <c>App.xaml</c> — la conocida.</para>
    ///
    /// <para><c>AppsUseLightTheme</c>: 0 = oscuro, 1 = claro. Es el valor que cambia el selector de
    /// «Modo de aplicación» de Windows, y existe desde Windows 10 1607. Si no está (Windows más
    /// viejo, o perfil nuevo que nunca lo tocó), no hay nada que decidir: oscuro.</para>
    /// </remarks>
    private void ApplySystemTheme()
    {
        try
        {
            object? value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);

            if (value is not int useLight || useLight != 1)
            {
                return; // oscuro, o no se pudo saber: queda la paleta por defecto de App.xaml
            }

            var light = new ResourceDictionary
            {
                Source = new Uri("Styles/Palette.Light.xaml", UriKind.Relative),
            };

            // Reemplaza la paleta oscura en su posición (0), para que los estilos de Theme.xaml
            // sigan resolviéndose después de ella.
            Resources.MergedDictionaries[0] = light;
        }
        catch (Exception ex)
        {
            // Sin tema claro se ve igual que siempre; romper el arranque por esto sería absurdo.
            System.Diagnostics.Debug.WriteLine($"No se pudo aplicar el tema del sistema: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _serilog?.Dispose();
        base.OnExit(e);
    }

    private static Serilog.Core.Logger BuildLogger()
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EasyFix",
            "logs");
        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "easyfix-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
    }

    private static ServiceProvider BuildServices(Serilog.Core.Logger serilog)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(serilog)));

        // Configuración: primero un appsettings.json junto al .exe (editable en el equipo del
        // cliente), y si no está, el recurso embebido. El .exe tiene que servir solo desde el USB.
        services.AddSingleton(LoadOptions());
        services.AddSingleton(sp => sp.GetRequiredService<EasyFixOptions>().Thresholds);
        services.AddSingleton(sp => sp.GetRequiredService<EasyFixOptions>().Classifier);
        services.AddSingleton(sp => sp.GetRequiredService<EasyFixOptions>().Branding);
        services.AddSingleton(sp => sp.GetRequiredService<EasyFixOptions>().Network);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileTree, PhysicalFileTree>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddSingleton<StartupClassifier>();
        services.AddSingleton<JunctionSafeCleaner>();
        services.AddSingleton<UndoEngine>();
        services.AddSingleton<JournalStore>();
        services.AddSingleton<HardwareAdvisor>();
        services.AddSingleton<SystemProbe>();
        services.AddSingleton<IWingetLocator, WingetLocator>();
        services.AddSingleton<IFileDownloader, HttpFileDownloader>();
        services.AddSingleton<IWingetBootstrapper, WingetBootstrapper>();
        services.AddSingleton<DefaultAppsConfigurator>();
        services.AddSingleton<DirectDownloadInstaller>();

        // La compuerta de red. Sin esto, un equipo sin internet producía tres diagnósticos falsos
        // seguidos culpando al catálogo de winget. Ver WingetService.InstallAsync.
        services.AddSingleton<IConnectivityCheck, HttpConnectivityCheck>();
        services.AddSingleton<IInstalledProgramsReader, InstalledProgramsReader>();
        services.AddSingleton<WingetService>();
        services.AddSingleton<ServiceConditionEvaluator>();

        // Reparación. RestorePointService y BitLockerService son las piezas que hacen reversible a
        // la herramienta: FixRunner aborta si el punto de restauración no se pudo verificar.
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<IBitLockerService, BitLockerService>();
        services.AddSingleton<FixRunner>();

        // Handlers de deshacer. Sin estos, UndoEngine reportaba "sin handler" para pasos que el
        // journal sí registraba: honesto, pero inútil.
        services.AddSingleton<IUndoHandler, RegistryValueUndoHandler>();
        services.AddSingleton<IUndoHandler, RegistryValueDeleteUndoHandler>();
        services.AddSingleton<IUndoHandler, RegistryBinaryValueUndoHandler>();
        services.AddSingleton<IUndoHandler, PowerPlanUndoHandler>();
        services.AddSingleton<IUndoHandler, BitLockerResumeUndoHandler>();

        services.AddSingleton<StartupEntryReader>();

        // Rendimiento («Mejorar rendimiento»). Orden: primero lo que libera espacio, después lo que
        // toca configuración.
        services.AddSingleton<IFix, TempCleanupFix>();
        services.AddSingleton<IFix, StartupDisableFix>();
        services.AddSingleton<IFix, DiskOptimizationFix>();
        services.AddSingleton<IFix, PowerPlanFix>();

        // Reparación («Reparar errores»). Primero lo que repara, después lo que mide, y al final lo
        // que puede requerir revertir una actualización.
        services.AddSingleton<IFix, SystemFileRepairFix>();
        services.AddSingleton<IFix, DiskCheckFix>();
        services.AddSingleton<IFix, WindowsUpdateResetFix>();
        services.AddSingleton<IFix, NetworkStackResetFix>();
        services.AddSingleton<IFix, ScheduleMemoryTestFix>();
        services.AddSingleton<IFix, RemoveCorrelatedUpdateFix>();

        // Busca e instala drivers de bajo riesgo desde el catálogo de Windows Update (COM tardío,
        // sin NuGet nuevo — ver la remark de WindowsUpdateDriverCatalog para el riesgo real: es
        // el código menos verificable del proyecto, nunca corrió contra un catálogo real).
        services.AddSingleton<IDriverCatalog, WindowsUpdateDriverCatalog>();
        services.AddSingleton<IFix, DriverUpdateFix>();

        // Los IDiagnosticCheck se registran acá a medida que se implementen; DiagnosticEngine
        // recibe la colección, que hoy está vacía a propósito.
        services.AddSingleton<DiagnosticEngine>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static EasyFixOptions LoadOptions()
    {
        string sideBySide = Path.Combine(AppContext.BaseDirectory, OptionsLoader.FileName);
        if (File.Exists(sideBySide))
        {
            return OptionsLoader.Parse(File.ReadAllText(sideBySide));
        }

        using Stream? embedded = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(OptionsLoader.EmbeddedResourceName);

        if (embedded is null)
        {
            throw new InvalidOperationException(
                $"No se encontró '{OptionsLoader.FileName}' junto al ejecutable ni embebido como " +
                $"'{OptionsLoader.EmbeddedResourceName}'. Sin configuración, la app no puede decidir " +
                "qué es seguro tocar, así que no arranca.");
        }

        using var reader = new StreamReader(embedded);
        return OptionsLoader.Parse(reader.ReadToEnd());
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<ILogger<App>>()?.LogError(e.Exception, "Excepción no manejada en la UI.");

        MessageBox.Show(
            $"EasyFix encontró un error inesperado y no aplicó el último cambio.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            $"El detalle quedó en el log. Si habías ejecutado una mejora, el registro de la corrida " +
            $"sigue en {FileJournalSink.DefaultDirectory} y se puede deshacer.",
            "EasyFix",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Marcar como manejada: la app sigue viva y el usuario puede deshacer.
        e.Handled = true;
    }
}
