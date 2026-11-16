using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Reputation;
using EasyFix.Core.Tests.Fakes;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El catálogo de reputación señala basura conocida SIN tocar herramientas de trabajo. Los tests
/// cuidan las dos direcciones: que lo dañino se detecte, y que un nombre parecido en un programa
/// legítimo NO se condene.
/// </summary>
public class ReputationEvaluatorTests
{
    private static ReputationCatalogEntry Confirmed(string product, string publisher = "") => new()
    {
        Product = product,
        Publisher = publisher,
        Category = "optimizador falso",
        Verdict = "confirmed",
    };

    private static InstalledProgram Program(string name, string? publisher = null, double? sizeMb = null) =>
        new(name, publisher, sizeMb, @"HKLM\...\" + name);

    // ---- Detección ----------------------------------------------------------------------------

    [Fact]
    public void ProductoYPublisherCoinciden_Matchea()
    {
        var matches = ReputationEvaluator.Match(
            new[] { Program("Restoro 2.6", "Restoro Limited") },
            new[] { Confirmed("Restoro", "Restoro") });

        ReputationMatch match = Assert.Single(matches);
        Assert.Equal(ReputationVerdict.ConfirmedHarmful, match.Verdict);
    }

    [Fact]
    public void ElMatchNoDistingueMayusculas()
    {
        var matches = ReputationEvaluator.Match(
            new[] { Program("RESTORO", "RESTORO LIMITED") },
            new[] { Confirmed("Restoro", "Restoro") });

        Assert.Single(matches);
    }

    // ---- No condenar de más -------------------------------------------------------------------

    [Fact]
    public void PublisherDeclaradoQueNoCoincide_NoMatchea()
    {
        // La regla (publisher, producto): un nombre coincidente con otro publisher no se condena.
        var matches = ReputationEvaluator.Match(
            new[] { Program("Driver Support Tool", "Contoso Legítimo S.A.") },
            new[] { Confirmed("Driver Support", "PC Drivers Headquarters") });

        Assert.Empty(matches);
    }

    [Fact]
    public void PublisherDeclaradoYProgramaSinPublisher_NoMatchea()
    {
        // Sin publisher en el registro, el nombre solo no alcanza para condenar.
        var matches = ReputationEvaluator.Match(
            new[] { Program("Driver Support") },
            new[] { Confirmed("Driver Support", "PC Drivers Headquarters") });

        Assert.Empty(matches);
    }

    [Fact]
    public void EntradaSinProducto_SeIgnoraEntera()
    {
        // Una entrada vacía matchearía todo lo instalado. Se ignora, no se interpreta.
        var matches = ReputationEvaluator.Match(
            new[] { Program("Cualquier Programa", "Cualquiera") },
            new[] { Confirmed(product: "", publisher: "") });

        Assert.Empty(matches);
    }

    [Fact]
    public void EntradaDemasiadoCorta_SeIgnora()
    {
        // El catálogo es editable a mano en el equipo del cliente; una entrada de 2-3 letras
        // condenaría por contains a media lista de programas.
        var matches = ReputationEvaluator.Match(
            new[] { Program("Microsoft Office", "Microsoft Corporation") },
            new[] { Confirmed(product: "off") });

        Assert.Empty(matches);
    }

    [Fact]
    public void HerramientaDeTrabajoNoEstaEnElCatalogoReal_NoSeCondena()
    {
        // El catálogo real no incluye acceso remoto, VPN ni antivirus de pago legítimos.
        var catalog = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings())).Reputation.KnownBad;

        var work = new[]
        {
            Program("TeamViewer", "TeamViewer Germany GmbH"),
            Program("AnyDesk", "AnyDesk Software GmbH"),
            Program("RustDesk", "RustDesk"),
            Program("ESET NOD32 Antivirus", "ESET, spol. s r.o."),
            Program("FortiClient VPN", "Fortinet, Inc."),
        };

        Assert.Empty(ReputationEvaluator.Match(work, catalog));
    }

    // ---- Veredictos ---------------------------------------------------------------------------

    [Fact]
    public void VeredictoIlegible_DegradaADudoso()
    {
        // Un typo en la config jamás puede convertir un programa en «confirmado dañino».
        var entry = new ReputationCatalogEntry
        {
            Product = "Cosa",
            Verdict = "confirmedd", // typo a propósito
        };

        var matches = ReputationEvaluator.Match(new[] { Program("Cosa Rara") }, new[] { entry });

        Assert.Equal(ReputationVerdict.Doubtful, Assert.Single(matches).Verdict);
    }

    // ---- Hallazgos ----------------------------------------------------------------------------

    [Fact]
    public void ToFindings_SeparaConfirmadoDeDudoso_YNuncaPrometeDesinstalarSolo()
    {
        var matches = new[]
        {
            new ReputationMatch(Program("Restoro", "Restoro Limited", 120),
                Confirmed("Restoro", "Restoro"), ReputationVerdict.ConfirmedHarmful),
            new ReputationMatch(Program("Driver Booster 11", "IObit", 300),
                new ReputationCatalogEntry { Product = "Driver Booster", Publisher = "IObit", Category = "actualizador de drivers", Verdict = "doubtful" },
                ReputationVerdict.Doubtful),
        };

        var findings = ReputationEvaluator.ToFindings(matches);

        Assert.Equal(2, findings.Count);

        Finding confirmed = Assert.Single(findings, f => f.CheckId == "reputation.confirmed");
        Assert.Equal(Severity.Warning, confirmed.Severity);
        Assert.Contains("Restoro", confirmed.Detail, StringComparison.Ordinal);
        Assert.Contains("no desinstala", confirmed.Detail, StringComparison.OrdinalIgnoreCase);

        Finding doubtful = Assert.Single(findings, f => f.CheckId == "reputation.doubtful");
        Assert.Equal(Severity.Info, doubtful.Severity);
        Assert.Contains("Driver Booster", doubtful.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ToFindings_ElTamanoMedidoViajaComoMetrica()
    {
        var matches = new[]
        {
            new ReputationMatch(Program("Restoro", "Restoro Limited", 120.4),
                Confirmed("Restoro", "Restoro"), ReputationVerdict.ConfirmedHarmful),
        };

        Finding finding = Assert.Single(ReputationEvaluator.ToFindings(matches));
        Metric metric = Assert.Single(finding.AllMetrics);

        Assert.Contains("120", metric.Value, StringComparison.Ordinal);
        Assert.Contains("MB", metric.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void SinMatches_SinHallazgos()
    {
        Assert.Empty(ReputationEvaluator.ToFindings(Array.Empty<ReputationMatch>()));
    }

    // ---- El catálogo embebido carga -----------------------------------------------------------

    [Fact]
    public void ElCatalogoEmbebido_TieneEntradasYTodasConProductoYCategoria()
    {
        var catalog = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings())).Reputation.KnownBad;

        Assert.True(catalog.Count >= 40, $"El catálogo tiene {catalog.Count} entradas; se esperaban 40+.");

        foreach (ReputationCatalogEntry entry in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Product));
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
            Assert.Contains(entry.Verdict, new[] { "confirmed", "doubtful" });
        }
    }
}
