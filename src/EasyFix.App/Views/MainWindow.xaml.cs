using System.Windows;
using System.Windows.Input;
using EasyFix.App.ViewModels;

namespace EasyFix.App.Views;

/// <summary>
/// Ventana principal. Solo maneja el chrome propio; toda decisión vive en el ViewModel.
/// </summary>
/// <remarks>
/// <para><b>Por qué NO se usa una librería de componentes acá.</b> v0.9.5 reemplazó esta clase por
/// el <c>FluentWindow</c> de WPF-UI para tener esquinas redondeadas, y llamó a
/// <c>ApplicationThemeManager.Apply</c> para sincronizar el tema. El resultado en un equipo real
/// fue que <b>no se podía leer nada</b>: sin un tema registrado en <c>App.xaml</c>, esa llamada
/// aplica el tema CLARO de la librería, que trae estilos implícitos de texto oscuro — encima del
/// fondo oscuro propio de <c>Theme.xaml</c>.</para>
///
/// <para>La lección no es «WPF-UI está mal», es que <b>una dependencia visual no se puede adoptar
/// sin poder ver el resultado</b>, y desde macOS no se puede. El layout horizontal con barra
/// lateral y dos columnas —que era lo que de verdad hacía falta— es XAML propio y se mantiene; lo
/// que se fue es la librería. Las esquinas redondeadas eran cosmética y no valían el riesgo.</para>
/// </remarks>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Con <c>WindowStyle="None"</c> el arrastre de la ventana hay que hacerlo a mano.</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
