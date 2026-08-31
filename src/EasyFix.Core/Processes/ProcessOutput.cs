namespace EasyFix.Core.Processes;

/// <summary>
/// Limpia la salida de un proceso de consola para que quepa en el log y se pueda leer.
/// </summary>
/// <remarks>
/// <para><b>El problema.</b> winget, DISM y sfc dibujan una barra de progreso animada reescribiendo la
/// misma línea con retornos de carro (<c>\r</c>). Capturada a un string, esa animación son cientos de
/// cuadros consecutivos: volcarla al log tal cual llena el archivo de basura y empuja fuera el único
/// renglón que importa. Truncar sin limpiar es peor todavía — se queda con los primeros 2000
/// caracteres, que son <em>todos</em> de la animación.</para>
///
/// <para><b>Qué se hace.</b> De cada tanda de cuadros se conserva el último, que es el estado final de
/// esa línea, y se descartan los renglones que son solo caracteres de animación. No se recorta ni se
/// reescribe el texto real: el log tiene que quedar completo.</para>
/// </remarks>
public static class ProcessOutput
{
    /// <summary>Caracteres con los que estas herramientas dibujan su animación.</summary>
    private const string SpinnerChars = @"-\|/█▒░■□▪▫ .";

    /// <summary>
    /// Colapsa la animación de progreso y descarta los renglones que no tienen contenido real.
    /// </summary>
    public static string Clean(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var lines = new List<string>();

        foreach (string physicalLine in output.Split('\n'))
        {
            // Un \r dentro de la línea significa que el proceso la reescribió: solo el último cuadro
            // refleja el estado con el que terminó.
            string lastFrame = physicalLine.Split('\r')[^1].Trim();

            if (lastFrame.Length == 0 || IsOnlySpinner(lastFrame))
            {
                continue;
            }

            lines.Add(lastFrame);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Recorta a <paramref name="max"/> caracteres, avisando cuánto se dejó afuera.
    /// </summary>
    /// <remarks>
    /// Decir cuántos caracteres faltan importa: sin eso no se sabe si se perdió una palabra o veinte
    /// pantallas de salida de DISM.
    /// </remarks>
    public static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max
            ? value
            : $"{value[..max]}… (+{value.Length - max} caracteres, ver la salida completa del proceso)";
    }

    /// <summary>Limpia y recorta en un paso. Es la forma en que lo consume el log.</summary>
    public static string ForLog(string? output, int max) => Truncate(Clean(output), max);

    private static bool IsOnlySpinner(string line)
    {
        foreach (char c in line)
        {
            if (!SpinnerChars.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
