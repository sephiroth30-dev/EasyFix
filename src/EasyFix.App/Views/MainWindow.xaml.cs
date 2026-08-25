using System.Windows;
using System.Windows.Input;
using EasyFix.App.ViewModels;

namespace EasyFix.App.Views;

/// <summary>
/// Ventana principal. Solo maneja el chrome propio; toda decisión vive en el ViewModel.
/// </summary>
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
