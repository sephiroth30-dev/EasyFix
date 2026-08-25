using Xunit;

namespace EasyFix.Core.Tests.Fakes;

/// <summary>
/// <c>[Fact]</c> que se omite cuando no se corre en Windows.
/// </summary>
/// <remarks>
/// La mayor parte de <c>EasyFix.Core</c> es lógica pura y corre en cualquier sistema, lo que permite
/// desarrollar y verificar sin una VM. Unos pocos tests sí dependen de Windows —variables de entorno
/// como <c>%SystemRoot%</c>, carpetas especiales como <c>System32</c>— y tienen que omitirse en vez de
/// fallar: un rojo permanente en la suite entrena a ignorar los rojos.
/// <para>
/// Se marcan también con <c>Trait("Category", "RequiresWindows")</c> para poder filtrarlos:
/// <c>dotnet test --filter Category!=RequiresWindows</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
[Trait("Category", "RequiresWindows")]
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Solo Windows: depende de variables de entorno o carpetas especiales de Windows.";
        }
    }
}
