using EasyFix.Core.Configuration;

namespace EasyFix.Core.Tests;

/// <summary>Localiza archivos del repo desde la salida del build.</summary>
internal static class TestPaths
{
    /// <summary>
    /// Ruta del <c>appsettings.json</c> real, subiendo desde la carpeta de salida.
    /// </summary>
    /// <remarks>
    /// Varios tests parsean el archivo de verdad y no una copia inventada: si alguien renombra una
    /// clave del archivo y no toca el modelo, o agrega una condición de servicio sin implementarla,
    /// esos tests lo agarran.
    /// </remarks>
    public static string RealAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, OptionsLoader.FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"No se encontró {OptionsLoader.FileName} subiendo desde {AppContext.BaseDirectory}.");
    }
}
