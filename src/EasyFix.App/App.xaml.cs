// Los ImplicitUsings de un proyecto WPF no incluyen System.IO (el set de WindowsDesktop es distinto
// al de una librería), así que va explícito.
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EasyFix.App.ViewModels;
using EasyFix.App.Views;
using EasyFix.Core.Abstractions;
using EasyFix.Core.Classification;
using EasyFix.Core.Cleaning;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
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

        _serilog = BuildLogger();
        _services = BuildServices(_serilog);

        _services.GetRequiredService<ILogger<App>>()
            .LogInformation("EasyFix iniciado. Journals en {Dir}.", FileJournalSink.DefaultDirectory);

        _services.GetRequiredService<MainWindow>().Show();
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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFileTree, PhysicalFileTree>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddSingleton<StartupClassifier>();
        services.AddSingleton<JunctionSafeCleaner>();
        services.AddSingleton<UndoEngine>();
        services.AddSingleton<HardwareAdvisor>();
        services.AddSingleton<ServiceConditionEvaluator>();

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
