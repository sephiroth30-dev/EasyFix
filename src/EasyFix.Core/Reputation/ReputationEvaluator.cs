using System.Globalization;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;

namespace EasyFix.Core.Reputation;

/// <summary>Qué tan segura es la condena del catálogo.</summary>
public enum ReputationVerdict
{
    /// <summary>
    /// Mala reputación documentada públicamente (sanciones FTC, remoción por antivirus serios,
    /// distribución engañosa probada). Se reporta como advertencia con la instrucción de
    /// desinstalar a mano; la oferta con casilla pre-marcada es del Tier B futuro.
    /// </summary>
    ConfirmedHarmful,

    /// <summary>
    /// Empresa real con producto de valor cuestionable o distribución engañosa. Se informa con la
    /// casilla desmarcada: la decisión es del técnico, que sabe si el cliente lo usa.
    /// </summary>
    Doubtful,
}

/// <summary>Un programa instalado que matcheó contra el catálogo de reputación.</summary>
public sealed record ReputationMatch(
    InstalledProgram Program,
    ReputationCatalogEntry Entry,
    ReputationVerdict Verdict);

/// <summary>
/// Cruza los programas instalados contra el catálogo de mala reputación. Lógica pura.
/// </summary>
/// <remarks>
/// <para><b>El match exige producto Y publisher</b> cuando el catálogo declara publisher — la misma
/// regla que el clasificador de startup: identificar por <c>(publisher, producto)</c>, no por glob de
/// nombre. «Driver Restore» como texto podría pisar un programa legítimo; «Driver Restore» de
/// «Driver Restore» como publisher, no.</para>
///
/// <para><b>Esto jamás desinstala nada.</b> Produce hallazgos para el reporte: lo confirmado-dañino
/// como advertencia con la instrucción de desinstalar a mano, lo dudoso como información (la oferta
/// con casillas es del Tier B futuro). Las
/// herramientas de trabajo (acceso remoto, VPN, antivirus de pago) no están en el catálogo a
/// propósito, y aunque un cliente tenga algo raro instalado, borrar software ajeno sin confirmación
/// es exactamente lo que esta app promete no hacer.</para>
/// </remarks>
public static class ReputationEvaluator
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static IReadOnlyList<ReputationMatch> Match(
        IReadOnlyList<InstalledProgram> programs,
        IReadOnlyList<ReputationCatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(catalog);

        var matches = new List<ReputationMatch>();

        foreach (InstalledProgram program in programs)
        {
            foreach (ReputationCatalogEntry entry in catalog)
            {
                if (!Matches(program, entry))
                {
                    continue;
                }

                // Un veredicto ilegible en la config degrada a Doubtful: un typo jamás puede
                // convertir un programa en «confirmado dañino».
                ReputationVerdict verdict = string.Equals(
                    entry.Verdict, "confirmed", StringComparison.OrdinalIgnoreCase)
                    ? ReputationVerdict.ConfirmedHarmful
                    : ReputationVerdict.Doubtful;

                matches.Add(new ReputationMatch(program, entry, verdict));
                break; // la primera entrada que matchea alcanza; dos condenas no suman nada
            }
        }

        return matches;
    }

    /// <summary>Hallazgos para el reporte: uno por lo confirmado, otro por lo dudoso.</summary>
    public static IReadOnlyList<Finding> ToFindings(IReadOnlyList<ReputationMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var findings = new List<Finding>();

        var confirmed = matches.Where(m => m.Verdict == ReputationVerdict.ConfirmedHarmful).ToList();
        var doubtful = matches.Where(m => m.Verdict == ReputationVerdict.Doubtful).ToList();

        if (confirmed.Count > 0)
        {
            findings.Add(new Finding(
                "reputation.confirmed",
                Severity.Warning,
                confirmed.Count == 1
                    ? "1 programa con mala reputación confirmada"
                    : $"{confirmed.Count} programas con mala reputación confirmada",
                "Estos programas tienen mala reputación documentada públicamente (sanciones, " +
                "distribución engañosa o comportamiento de adware). Suelen entrar «de regalo» con " +
                "otros instaladores, no porque alguien los quisiera. Conviene desinstalarlos desde " +
                "Configuración → Aplicaciones: " + Describe(confirmed) + ". EasyFix no desinstala " +
                "programas por su cuenta: la lista es para que lo hagas con un clic por programa, " +
                "sabiendo qué es cada cosa.",
                confirmed.Select(ToMetric).ToList()));
        }

        if (doubtful.Count > 0)
        {
            findings.Add(new Finding(
                "reputation.doubtful",
                Severity.Info,
                doubtful.Count == 1
                    ? "1 programa de valor dudoso"
                    : $"{doubtful.Count} programas de valor dudoso",
                "Productos de empresas reales pero de utilidad cuestionable, o que suelen llegar " +
                "empaquetados con otros instaladores. Si el cliente no los usa a propósito, se pueden " +
                "quitar: " + Describe(doubtful) + ". Si los usa para trabajar, se quedan — esa " +
                "decisión es tuya y del cliente, no de esta herramienta.",
                doubtful.Select(ToMetric).ToList()));
        }

        return findings;
    }

    /// <summary>Largo mínimo del patrón de producto para que una entrada cuente.</summary>
    /// <remarks>
    /// El catálogo es editable en el equipo del cliente sin recompilar, y el match es por
    /// «contains»: una entrada de dos o tres letras agregada a mano condenaría media lista de
    /// programas. Mejor ignorar la entrada corta que condenar por accidente.
    /// </remarks>
    private const int MinProductPatternLength = 4;

    private static bool Matches(InstalledProgram program, ReputationCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Product) ||
            entry.Product.Trim().Length < MinProductPatternLength)
        {
            return false; // una entrada vacía o demasiado corta matchearía de más: se ignora entera
        }

        if (!program.DisplayName.Contains(entry.Product, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Publisher declarado en el catálogo → tiene que coincidir también. Si el programa no
        // declara publisher en el registro, el match por nombre solo NO alcanza para condenarlo.
        if (!string.IsNullOrWhiteSpace(entry.Publisher))
        {
            return program.Publisher is not null &&
                   program.Publisher.Contains(entry.Publisher, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static Metric ToMetric(ReputationMatch match)
    {
        string label = match.Program.Publisher is null
            ? match.Program.DisplayName
            : $"{match.Program.DisplayName} ({match.Program.Publisher})";

        string value = match.Entry.Category;

        if (match.Program.EstimatedSizeMb is double mb && mb > 0)
        {
            value += $" · {mb.ToString("0", Es)} MB";
        }

        return new Metric(label, value);
    }

    private static string Describe(IReadOnlyList<ReputationMatch> matches) =>
        string.Join(", ", matches.Select(m => m.Program.DisplayName).Take(6)) +
        (matches.Count > 6 ? "…" : string.Empty);
}
