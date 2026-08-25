using EasyFix.Core.Configuration;

namespace EasyFix.Core.Classification;

/// <summary>
/// Decide si una entrada de autoarranque se puede desactivar sola, con permiso, o nunca.
/// </summary>
/// <remarks>
/// <para><b>Lo que este clasificador NO hace: match por nombre de archivo.</b> Un patrón como
/// <c>*VPN*</c> protegería a un malware llamado <c>MyVPN.exe</c> y dejaría desprotegido a
/// <c>PulseSecure.exe</c>. Un nombre de archivo lo elige quien escribió el archivo; un certificado
/// lo emite una CA que verificó la identidad de la empresa.</para>
///
/// <para><b>Nota sobre el nombre de producto.</b> La Capa 2 sí compara el producto por texto, pero
/// solo <i>después</i> de que el certificado estableció quién es el publisher. El certificado
/// responde "¿es realmente Adobe?" y el nombre de producto solo elige "¿cuál de los programas de
/// Adobe?". Eso es distinto de usar el nombre para decidir identidad.</para>
///
/// <para>Sin estado y sin dependencias de Windows: se testea entero con DTOs.</para>
/// </remarks>
public sealed class StartupClassifier
{
    private readonly ClassifierOptions _options;

    public StartupClassifier(ClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Clasifica una entrada. Las capas se evalúan en orden y la primera que aplica gana.
    /// Si ninguna aplica, el resultado es <see cref="ClassificationTier.NeedsApproval"/>:
    /// desconocido = pedir permiso.
    /// </summary>
    public StartupVerdict Classify(StartupCandidate candidate, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        // ---- Capa 1: bloqueo duro -----------------------------------------------------------
        // Consultas verificables, sin nombres. Cualquiera que dé positivo hace la entrada intocable.

        if (candidate.IsRegisteredSecurityProduct)
        {
            return new StartupVerdict(
                ClassificationTier.HardBlocked,
                "Es un producto de seguridad registrado en Windows. Desactivarlo dejaría el equipo sin protección.");
        }

        if (IsProtectedPublisher(candidate.Signature))
        {
            return new StartupVerdict(
                ClassificationTier.HardBlocked,
                $"Está firmado por {candidate.Signature.PublisherOrganization}, un publisher protegido " +
                "(componente de Windows, driver, VPN, seguridad o respaldo).");
        }

        if (IsUnderProtectedPath(candidate.ExecutablePath))
        {
            return new StartupVerdict(
                ClassificationTier.HardBlocked,
                "El ejecutable está en una carpeta del sistema. Es un componente de Windows.");
        }

        if (_options.HardBlock.BlockIfHasRunningDependents && candidate.Dependents.Count > 0)
        {
            string list = string.Join(", ", candidate.Dependents.Take(3));
            string more = candidate.Dependents.Count > 3 ? $" y {candidate.Dependents.Count - 3} más" : string.Empty;
            return new StartupVerdict(
                ClassificationTier.HardBlocked,
                $"Hay servicios en ejecución que dependen de este: {list}{more}. Detenerlo los rompería.");
        }

        if (_options.HardBlock.BlockIfDriverAssociated && candidate.HasAssociatedDriver)
        {
            return new StartupVerdict(
                ClassificationTier.HardBlocked,
                "Tiene un driver asociado (audio, video, chipset o red). Desactivarlo puede dejar hardware sin funcionar.");
        }

        // ---- Sin firma válida: nunca automático ---------------------------------------------
        // Va antes de la Capa 2 a propósito: si la firma no valida, el nombre que declare el binario
        // no es evidencia de nada. Un ejecutable sin firmar en el arranque además es señal de malware.

        if (!candidate.Signature.IsValid)
        {
            return new StartupVerdict(
                ClassificationTier.NeedsApproval,
                "El ejecutable no tiene una firma digital válida, así que no se puede verificar quién lo hizo.",
                WhatYouLose: "Desconocido — revisá qué es antes de desactivarlo.",
                PossibleMalware: true);
        }

        // ---- Capa 2: lista blanca positiva --------------------------------------------------

        AllowlistEntry? match = FindAllowlistMatch(candidate, context);
        if (match is not null)
        {
            return new StartupVerdict(
                ClassificationTier.AutoSafe,
                $"{match.Product} de {match.Publisher}: no hace falta que arranque con Windows.",
                WhatYouLose: match.Loses);
        }

        // ---- Capa 3: default -----------------------------------------------------------------

        return new StartupVerdict(
            ClassificationTier.NeedsApproval,
            "No está en la lista de programas que se pueden desactivar sin riesgo, así que hace falta tu confirmación.",
            WhatYouLose: "Podría ser algo que el equipo necesite.");
    }

    /// <summary>
    /// Clasifica un lote conservando el orden de entrada.
    /// </summary>
    public IReadOnlyList<(StartupCandidate Candidate, StartupVerdict Verdict)> ClassifyAll(
        IEnumerable<StartupCandidate> candidates,
        SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Select(c => (c, Classify(c, context))).ToList();
    }

    private bool IsProtectedPublisher(SignatureInfo signature)
    {
        // Una firma inválida no puede otorgar protección: cualquiera puede escribir "Microsoft
        // Windows" en un binario sin firmar. Solo un certificado que valida cuenta como evidencia.
        if (!signature.IsValid || string.IsNullOrWhiteSpace(signature.PublisherOrganization))
        {
            return false;
        }

        return _options.HardBlock.ProtectedCertificatePublishers
            .Any(p => string.Equals(p, signature.PublisherOrganization, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Comparación de prefijo a nivel de segmento de ruta.
    /// </summary>
    /// <remarks>
    /// Un <c>StartsWith</c> pelado haría que <c>C:\Windows\System32Evil\x.exe</c> matcheara el
    /// prefijo <c>C:\Windows\System32</c>. El prefijo tiene que terminar en separador o coincidir
    /// exacto.
    /// </remarks>
    private bool IsUnderProtectedPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string normalized = NormalizeForComparison(executablePath);

        foreach (string prefix in _options.HardBlock.ProtectedPathPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                continue;
            }

            string normalizedPrefix = NormalizeForComparison(prefix).TrimEnd('\\');

            if (normalized.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(normalizedPrefix + '\\', StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForComparison(string path) =>
        path.Replace('/', '\\').Trim();

    private AllowlistEntry? FindAllowlistMatch(StartupCandidate candidate, SystemContext context)
    {
        string? publisher = candidate.Signature.PublisherOrganization;
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        foreach (AllowlistEntry entry in _options.AutoDisableAllowlist)
        {
            if (!string.Equals(entry.Publisher, publisher, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Teams personal se puede desactivar; Teams corporativo no. En equipo de dominio la
            // entrada se cae a Capa 3 en vez de matchear.
            if (entry.OnlyIfNotDomainJoined && context.IsDomainJoined)
            {
                continue;
            }

            if (ProductMatches(entry.Product, candidate))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Compara el producto configurado contra los metadatos y el nombre visible del candidato.
    /// Solo se llega acá con el publisher ya verificado por certificado.
    /// </summary>
    private static bool ProductMatches(string configuredProduct, StartupCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(configuredProduct))
        {
            return false;
        }

        // ProductName (metadatos del binario) es la fuente preferida; DisplayName (lo que puso la
        // clave de registro) es el fallback, y lo puede escribir cualquiera — por eso solo se
        // consulta dentro de un publisher ya verificado.
        return ContainsInsensitive(candidate.ProductName, configuredProduct)
            || ContainsInsensitive(candidate.DisplayName, configuredProduct);
    }

    private static bool ContainsInsensitive(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
