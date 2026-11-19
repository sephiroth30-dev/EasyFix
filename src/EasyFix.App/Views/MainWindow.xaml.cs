using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EasyFix.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EasyFix.App.Views;

/// <summary>
/// Ventana principal. Solo maneja el chrome propio; toda decisión vive en el ViewModel.
/// </summary>
/// <remarks>
/// <see cref="FluentWindow"/> (WPF-UI, lepoco/wpfui en GitHub) en vez de <see cref="Window"/> — es
/// lo único que hace falta para las esquinas redondeadas nativas de Windows 11
/// (<c>WindowCornerPreference</c> en el XAML). No se tocó <c>Mica</c>/<c>Acrylic</c>: eso exige
/// hacer transparentes partes del fondo que hoy son sólidas a propósito (tema oscuro fijo,
/// documentado), y no hay manera de verificar el resultado visual desde una Mac. Esquinas
/// redondeadas es un cambio aislado y seguro; el fondo traslúcido no se intentó a ciegas.
/// </remarks>
public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        // Sincroniza el tema interno de WPF-UI con el oscuro fijo de la app, y registra el mismo
        // acento de Theme.xaml — así los ui:SymbolIcon y cualquier otro control de la librería
        // leen el color correcto en vez del acento azul por defecto de WPF-UI.
        ApplicationThemeManager.Apply(this);
        ApplicationAccentColorManager.Apply(
            (Color)ColorConverter.ConvertFromString("#4C9AFF"),
            ApplicationTheme.Dark);
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
