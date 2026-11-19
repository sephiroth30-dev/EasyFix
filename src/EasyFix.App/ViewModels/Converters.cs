using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyFix.App.ViewModels;

/// <summary>Muestra un panel solo cuando la pantalla activa coincide con el parámetro.</summary>
/// <example><c>Visibility="{Binding CurrentScreen, Converter={StaticResource ScreenVis}, ConverterParameter=Home}"</c></example>
public sealed class ScreenToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Screen current ||
            !Enum.TryParse(parameter as string, ignoreCase: true, out Screen expected))
        {
            return Visibility.Collapsed;
        }

        return current == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("El binding de pantalla es de una sola dirección.");
}

/// <summary>Oculta el elemento cuando el texto está vacío, para que no deje un hueco.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Oculta el elemento cuando el booleano es <c>true</c>. Es el inverso de
/// <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>.
/// </summary>
/// <remarks>
/// Se usa para el porcentaje: mientras la barra está en modo indeterminado no hay número que
/// mostrar, y un «0 %» fijo haría creer que la operación está trabada.
/// </remarks>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Niega un booleano. Se usa para <c>IsEnabled</c> a partir de <c>FixesBlocked</c>: cuando los
/// arreglos están bloqueados, el botón se apaga.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

/// <summary>
/// Convierte un color <c>#RRGGBB</c> de la configuración en un pincel.
/// </summary>
/// <remarks>
/// <para>Lo usa el monograma que acompaña a la historia de cada programa: un círculo con el color
/// de la marca y su inicial. No se dibuja el logo real porque cuando se cuenta la historia el
/// programa todavía se está instalando —así que su ejecutable no existe para sacarle el icono— y
/// empaquetar logos ajenos traería binarios de marcas registradas al repo.</para>
///
/// <para>Un valor inválido o ausente cae en el color de acento del tema, que siempre existe: un
/// typo en <c>appsettings.json</c> no puede dejar un hueco negro en la pantalla.</para>
/// </remarks>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && hex.Length > 0)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch (FormatException)
            {
                // Cae al acento, abajo.
            }
        }

        return Application.Current?.TryFindResource("AccentBrush") as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Convierte la ruta de un instalador en su icono, para mostrarlo como logo del programa.
/// </summary>
/// <remarks>
/// La extracción vive acá y no en el ViewModel porque <c>BitmapSource</c> es un tipo de WPF: el
/// ViewModel tiene que compilarse sin WPF para que las pruebas end-to-end corran desde cualquier
/// sistema. Si no se puede sacar el icono devuelve <c>null</c> y la vista cae al monograma.
/// </remarks>
public sealed class InstallerPathToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? InstallerIcon.TryExtract(path) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <c>true</c> cuando el valor es nulo. Se usa para poner la barra de una fila en modo
/// indeterminado mientras todavía no hay bytes que contar: sin total conocido, un porcentaje
/// sería inventado.
/// </summary>
public sealed class NullToTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
