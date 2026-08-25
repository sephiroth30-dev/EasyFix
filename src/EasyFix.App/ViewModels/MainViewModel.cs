using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyFix.Core.Configuration;

namespace EasyFix.App.ViewModels;

/// <summary>
/// Pantalla principal: resumen del equipo y las tres acciones.
/// </summary>
/// <remarks>
/// Sin lógica de negocio. Los tres comandos van a delegar en servicios de <c>EasyFix.Core</c>; hasta
/// que esos servicios existan, informan que la función no está implementada en vez de simular
/// trabajo. Una barra de progreso que no hace nada es peor que un mensaje honesto: en el equipo de un
/// cliente, hace creer que se aplicó algo.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly EasyFixOptions _options;

    public MainViewModel(EasyFixOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        ImproveCommand = new RelayCommand(() => NotImplementedYet(
            "Mejorar rendimiento",
            "Falta el motor de diagnóstico (WMI) y el servicio de punto de restauración. " +
            "Sin punto de restauración verificado, la app no aplica ningún cambio."));

        RepairCommand = new RelayCommand(() => NotImplementedYet(
            "Reparar errores",
            "Falta el escaneo profundo: DISM, SFC y chkdsk."));

        InstallAppsCommand = new RelayCommand(() => NotImplementedYet(
            "Instalar programas",
            $"Faltan las llamadas a winget. Hay {_options.WingetPackages.Count} paquetes configurados."));
    }

    public IRelayCommand ImproveCommand { get; }

    public IRelayCommand RepairCommand { get; }

    public IRelayCommand InstallAppsCommand { get; }

    /// <summary>Resumen del equipo. Lo va a llenar el escaneo rápido.</summary>
    [ObservableProperty]
    private string _systemSummary = "Analizando el equipo…";

    /// <summary>Línea de estado bajo los botones.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary><c>true</c> cuando el mensaje de estado es una advertencia, para pintarlo distinto.</summary>
    [ObservableProperty]
    private bool _statusIsWarning;

    /// <summary>Cantidad de paquetes de winget configurados. Se muestra en el subtítulo del botón.</summary>
    public int ConfiguredPackageCount => _options.WingetPackages.Count;

    private void NotImplementedYet(string action, string detail)
    {
        StatusIsWarning = true;
        StatusMessage = $"«{action}» todavía no está implementado. {detail}";
    }
}
