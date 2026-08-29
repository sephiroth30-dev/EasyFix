using System.Globalization;
using System.Windows;
using System.Windows.Data;

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
