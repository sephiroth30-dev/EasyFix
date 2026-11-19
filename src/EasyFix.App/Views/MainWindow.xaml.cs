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

        // El análisis arranca solo al abrir, como un inventario del equipo. Antes había que
        // apretar «Analizar» primero, y todo lo demás quedaba bloqueado hasta hacerlo: un técnico
        // apretaba «Buscar controladores» y la app contestaba «primero hay que analizar el
        // equipo», que se lee como que el botón no funciona. El análisis es de solo lectura y
        // tarda segundos, así que no hay motivo para pedirlo a mano.
        Loaded += async (_, _) =>
        {
            if (viewModel.AnalyzeCommand.CanExecute(null))
            {
                await viewModel.AnalyzeCommand.ExecuteAsync(null);
            }
        };
    }

    /// <summary>
    /// Con <c>WindowStyle="None"</c> el arrastre y el doble clic para maximizar hay que hacerlos
    /// a mano.
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ClickCount == 1)
        {
            // Arrastrar una ventana maximizada la restaura y la engancha al cursor, igual que
            // hace Windows con cualquier ventana normal.
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            DragMove();
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// Alterna maximizado, respetando el área de trabajo.
    /// </summary>
    /// <remarks>
    /// Con <c>WindowStyle="None"</c>, maximizar cubre la barra de tareas: Windows no aplica el
    /// ajuste que haría con una ventana de chrome normal. Acotar <c>MaxHeight</c>/<c>MaxWidth</c>
    /// al <c>WorkArea</c> antes de maximizar lo evita. Se lee en cada maximización y no una sola
    /// vez al abrir: el técnico puede mover la ventana a otro monitor, o cambiar la resolución
    /// del equipo que está reparando.
    /// </remarks>
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxHeight = double.PositiveInfinity;
            MaxWidth = double.PositiveInfinity;
            return;
        }

        System.Windows.Rect workArea = SystemParameters.WorkArea;
        MaxHeight = workArea.Height;
        MaxWidth = workArea.Width;
        WindowState = WindowState.Maximized;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
