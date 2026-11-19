using System.Text.RegularExpressions;
using EasyFix.Core.Tests.Fakes;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Las dos paletas tienen que definir EXACTAMENTE las mismas claves.
/// </summary>
/// <remarks>
/// <para>Es el único test de este proyecto que mira XAML, y existe por un motivo concreto: una
/// clave que falte en una sola de las dos paletas revienta —o peor, deja algo ilegible— solo
/// cuando el equipo del cliente está en ese tema. Ninguna otra cosa lo detecta: compila igual,
/// y desde macOS no se puede ver renderizado.</para>
///
/// <para>Hereda del incidente de v0.9.5→v0.10.1, donde una librería de temas dejó la app ilegible
/// en un equipo real. Lo que se puede verificar sin ver la pantalla, se verifica.</para>
/// </remarks>
public sealed class PaletteParityTests
{
    private static string PaletteDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "EasyFix.App", "Styles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("No se encontró src/EasyFix.App/Styles.");
    }

    /// <summary>Todas las <c>x:Key</c> declaradas en un diccionario de recursos.</summary>
    private static HashSet<string> KeysIn(string fileName)
    {
        string xaml = File.ReadAllText(Path.Combine(PaletteDirectory(), fileName));

        return Regex.Matches(xaml, """x:Key="([^"]+)" """.TrimEnd())
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void LasDosPaletasDefinenLasMismasClaves()
    {
        HashSet<string> dark = KeysIn("Palette.Dark.xaml");
        HashSet<string> light = KeysIn("Palette.Light.xaml");

        Assert.NotEmpty(dark);

        var soloEnOscura = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var soloEnClara = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(
            soloEnOscura.Count == 0 && soloEnClara.Count == 0,
            $"Las paletas divergen. Solo en oscura: [{string.Join(", ", soloEnOscura)}]. " +
            $"Solo en clara: [{string.Join(", ", soloEnClara)}].");
    }

    [Fact]
    public void ThemeNoDefineColoresPropios()
    {
        // Theme.xaml tiene que consumir la paleta por nombre, nunca declarar un color literal:
        // un #RRGGBB suelto ahí se vería igual en los dos temas y rompería uno de los dos.
        string theme = File.ReadAllText(Path.Combine(PaletteDirectory(), "Theme.xaml"));

        var literales = Regex.Matches(theme, @"Value=""#[0-9A-Fa-f]{6,8}""")
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            literales.Count == 0,
            $"Theme.xaml tiene colores literales, que rompen uno de los dos temas: " +
            $"{string.Join(", ", literales)}. Movelos a las dos paletas.");
    }

    [Fact]
    public void AmbasPaletasDefinenElTextoSobreElAcento()
    {
        // OnAccentBrush es la clave que más fácil se olvida y la que deja el botón principal
        // ilegible: el acento es claro en un tema y oscuro en el otro.
        Assert.Contains("OnAccentBrush", KeysIn("Palette.Dark.xaml"));
        Assert.Contains("OnAccentBrush", KeysIn("Palette.Light.xaml"));
    }
}
