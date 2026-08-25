namespace EasyFix.Core.Safety;

/// <summary>
/// Verifica que una ruta esté contenida en la raíz esperada antes de borrar o copiar.
/// </summary>
/// <remarks>
/// Es la última línea de defensa contra dos cosas: un <c>..</c> que se escapa del árbol, y un
/// reparse point cuyo destino apunta afuera. Cualquier ruta que no pase por acá no se toca.
/// </remarks>
public static class PathGuard
{
    /// <summary>
    /// <c>true</c> si <paramref name="candidate"/> es la raíz o está por debajo.
    /// </summary>
    /// <remarks>
    /// La comparación es por segmento: un <c>StartsWith</c> pelado haría que
    /// <c>C:\Temp2\x</c> pasara como contenido en <c>C:\Temp</c>.
    /// </remarks>
    public static bool IsWithin(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string c = Normalize(candidate);
        string r = Normalize(root);

        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + '\\', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Igual que <see cref="IsWithin"/> pero tira excepción. Para los puntos donde seguir con una
    /// ruta fuera de la raíz sería un bug con consecuencias, no una advertencia.
    /// </summary>
    public static void EnsureWithin(string candidate, string root)
    {
        if (!IsWithin(candidate, root))
        {
            throw new UnauthorizedAccessException(
                $"La ruta '{candidate}' está fuera de la raíz permitida '{root}'. Operación cancelada.");
        }
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').Trim();
}
