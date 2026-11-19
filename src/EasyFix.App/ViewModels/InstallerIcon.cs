using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace EasyFix.App.ViewModels;

/// <summary>
/// Saca el icono real de un programa del instalador que acabamos de descargar.
/// </summary>
/// <remarks>
/// <para><b>Por qué de ahí y no de otro lado.</b> El logo auténtico de cada programa está dentro
/// de su propio instalador, que EasyFix ya tiene en disco en el momento exacto en que hace falta
/// mostrarlo. Las alternativas eran peores: empaquetar logos ajenos mete binarios de marcas
/// registradas al repo y hay que mantenerlos; bajarlos de cada sitio web agrega descargas que
/// pueden fallar; y sacarlo del programa instalado no sirve porque todavía se está
/// instalando.</para>
///
/// <para><b>Solo funciona con los de descarga directa</b> (Chrome, 7-Zip, RustDesk): de los que
/// instala winget no tenemos el archivo. Para el resto queda el monograma, que por eso no se
/// elimina.</para>
///
/// <para><b>P/Invoke a shell32, sin paquetes nuevos.</b> <c>ExtractIconEx</c> es la API de Windows
/// de toda la vida para esto, y <c>CreateBitmapSourceFromHIcon</c> ya viene con WPF. Se evita
/// <c>System.Drawing.Common</c>, que sería una dependencia entera para una sola llamada.</para>
///
/// <para><b>SIN VERIFICAR en Windows.</b> Es P/Invoke: el compilador no valida nada de esto. Si
/// falla, devuelve <c>null</c> y se ve el monograma — nunca rompe la pantalla.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class InstallerIcon
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// El icono del archivo, o <c>null</c> si no se pudo sacar. Nunca tira.
    /// </summary>
    public static BitmapSource? TryExtract(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        IntPtr large = IntPtr.Zero;
        IntPtr small = IntPtr.Zero;

        try
        {
            // El índice 0 es el icono principal del archivo, que es el del programa.
            if (ExtractIconEx(filePath, 0, out large, out small, 1) <= 0 || large == IntPtr.Zero)
            {
                return null;
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Congelarlo lo hace utilizable desde cualquier hilo y evita que WPF lo copie en
            // cada render: la extracción ocurre en un hilo del pool, no en el de la interfaz.
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            // Un instalador sin icono, un formato que la API no entiende, o cualquier otra cosa:
            // se cae al monograma. Un logo es decoración — nunca puede tumbar una instalación.
            return null;
        }
        finally
        {
            if (large != IntPtr.Zero) { DestroyIcon(large); }
            if (small != IntPtr.Zero) { DestroyIcon(small); }
        }
    }
}
