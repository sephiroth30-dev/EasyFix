namespace EasyFix.Core.Classification;

/// <summary>
/// Extrae la organización del subject de un certificado X.509.
/// </summary>
/// <remarks>
/// El subject viene como <c>CN=Spotify AB, O=Spotify AB, L=Stockholm, C=SE</c>. Lo que importa es
/// <c>O=</c> (la organización), no <c>CN=</c>: el CN a veces trae el nombre del producto y a veces
/// el de la empresa, mientras que la O es la entidad legal que la CA verificó.
/// <para>
/// No se usa <c>string.Split(',')</c>: un valor puede venir entrecomillado y contener comas
/// —<c>O="Foo, Inc.", L=...</c>— y el split partiría el nombre al medio.
/// </para>
/// </remarks>
public static class CertificateSubject
{
    /// <summary>
    /// Devuelve el valor de <c>O=</c>, o <c>null</c> si el subject no lo trae o viene vacío.
    /// </summary>
    public static string? ExtractOrganization(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        foreach ((string key, string value) in EnumerateRelativeNames(subject))
        {
            if (string.Equals(key, "O", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>
    /// Recorre los pares <c>clave=valor</c> del subject respetando comillas y escapes.
    /// </summary>
    private static IEnumerable<(string Key, string Value)> EnumerateRelativeNames(string subject)
    {
        int i = 0;
        while (i < subject.Length)
        {
            // Saltar separadores y espacios sobrantes.
            while (i < subject.Length && (subject[i] == ',' || subject[i] == ';' || subject[i] == ' '))
            {
                i++;
            }

            int keyStart = i;
            while (i < subject.Length && subject[i] != '=')
            {
                // Un separador antes del '=' significa subject mal formado: se corta.
                if (subject[i] == ',' || subject[i] == ';')
                {
                    yield break;
                }

                i++;
            }

            if (i >= subject.Length)
            {
                yield break;
            }

            string key = subject[keyStart..i].Trim();
            i++; // consumir el '='

            var value = new System.Text.StringBuilder();
            bool inQuotes = false;

            while (i < subject.Length)
            {
                char c = subject[i];

                if (c == '\\' && i + 1 < subject.Length)
                {
                    value.Append(subject[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    i++;
                    continue;
                }

                if (!inQuotes && (c == ',' || c == ';'))
                {
                    break;
                }

                value.Append(c);
                i++;
            }

            yield return (key, value.ToString().Trim());
        }
    }
}
