using System.IO;
using System.Reflection;
using EasyFix.App.ViewModels;
using Xunit;

namespace EasyFix.App.Tests;

/// <summary>
/// El ViewModel no puede depender de WPF. Es lo que permite probarlo.
/// </summary>
/// <remarks>
/// <para>Estas pruebas compilan <c>MainViewModel.cs</c> y <c>AppChoice.cs</c> directamente, sin
/// referenciar el proyecto WPF: referenciarlo arrastra el runtime
/// <c>Microsoft.WindowsDesktop.App</c>, que no existe en macOS, y las pruebas morían al arrancar
/// con «No frameworks were found» — habrían corrido solo en el CI de Windows, justo cuando ya es
/// tarde para corregir.</para>
///
/// <para>Ese truco depende de una condición: que el ViewModel no toque <c>System.Windows</c>. Si
/// alguien mete un tipo de WPF ahí, el proyecto de pruebas deja de compilar y el mensaje es
/// críptico. Este test lo dice con todas las letras, y además marca el problema de diseño de
/// fondo: lógica mezclada con presentación.</para>
/// </remarks>
public sealed class ViewModelSinWpfTests
{
    private static string ViewModelsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "EasyFix.App", "ViewModels");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró src/EasyFix.App/ViewModels.");
    }

    [Theory]
    [InlineData("MainViewModel.cs")]
    [InlineData("AppChoice.cs")]
    public void LosFuentesQueSeTesteanNoUsanWpf(string fileName)
    {
        string source = File.ReadAllText(Path.Combine(ViewModelsDirectory(), fileName));

        Assert.False(
            source.Contains("System.Windows", StringComparison.Ordinal),
            $"{fileName} usa System.Windows. Eso rompe estas pruebas (exigen el runtime de WPF, " +
            "que no existe en macOS) y además mezcla presentación con lógica. Lo que necesite WPF " +
            "va en la vista o en un converter, no en el ViewModel.");
    }

    [Fact]
    public void ElEnsambladoDePruebasNoCargaWpf()
    {
        // Comprobación desde el otro lado: si algo arrastrara WPF, estaría acá.
        var wpf = Assembly.GetExecutingAssembly()
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null)
            .Where(a => a.Name!.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase)
                     || a.Name!.Contains("WindowsBase", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name)
            .ToList();

        Assert.True(wpf.Count == 0, $"Las pruebas arrastran WPF: {string.Join(", ", wpf)}");
    }
}
