using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using EasyFix.Core.Recommendations;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// La regla de v0.9.0: un campo del snapshot sin medir es <c>null</c>, y <c>null</c> falla cerrado.
/// Estos tests existen porque las cuatro versiones anteriores salieron del mismo defecto — afirmar
/// cosas que nadie midió — y la última vía que quedaba era que un timeout de sonda se volviera
/// <c>false</c>/<c>0</c>.
/// </summary>
public class SnapshotHonestyTests
{
    private static readonly ThresholdOptions Thresholds = new();

    // ---- IsSsd: Unknown ya no es «mecánico» ------------------------------------------------

    [Theory]
    [InlineData(DiskMedia.Ssd, true)]
    [InlineData(DiskMedia.Scm, true)]
    [InlineData(DiskMedia.Hdd, false)]
    public void IsSsd_ConMediaIdentificado_Afirma(DiskMedia media, bool expected)
    {
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = media };
        Assert.Equal(expected, snapshot.IsSsd);
    }

    [Fact]
    public void IsSsd_ConMediaUnknown_NoAfirmaNada()
    {
        // Antes devolvía false: un disco sin identificar se trataba como mecánico, lo que suprimía
        // la recomendación de SSD y afectaba la condición de SysMain.
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Unknown };
        Assert.Null(snapshot.IsSsd);
    }

    // ---- ServiceConditionEvaluator: «no medido» no abre compuertas --------------------------

    [Fact]
    public void Spooler_ConImpresorasSinMedir_NoSeOfrece()
    {
        var evaluator = new ServiceConditionEvaluator();
        var candidate = new ServiceCandidate
        {
            Name = "Spooler",
            Loses = "Imprimir",
            OnlyIf = ServiceConditionEvaluator.NoPrintersInstalled,
        };

        ServiceOffer offer = evaluator.Evaluate(candidate, new SystemSnapshot { HasPrinters = null });

        Assert.False(offer.Offer);
        Assert.Contains("No se pudo comprobar", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Spooler_SinImpresorasMedido_SiSeOfrece()
    {
        var evaluator = new ServiceConditionEvaluator();
        var candidate = new ServiceCandidate
        {
            Name = "Spooler",
            Loses = "Imprimir",
            OnlyIf = ServiceConditionEvaluator.NoPrintersInstalled,
        };

        Assert.True(evaluator.Evaluate(candidate, new SystemSnapshot { HasPrinters = false }).Offer);
    }

    [Fact]
    public void Bluetooth_SinMedir_NoSeOfrece()
    {
        var evaluator = new ServiceConditionEvaluator();
        var candidate = new ServiceCandidate
        {
            Name = "bthserv",
            Loses = "Bluetooth",
            OnlyIf = ServiceConditionEvaluator.NoBluetoothAdapter,
        };

        Assert.False(evaluator.Evaluate(candidate, new SystemSnapshot { HasBluetoothAdapter = null }).Offer);
    }

    // ---- Antivirus: null = no medido, no «cero antivirus» ------------------------------------

    [Fact]
    public void Antivirus_SinMedir_NoGeneraRecomendacion()
    {
        var advisor = new HardwareAdvisor(Thresholds);
        var snapshot = new SystemSnapshot { ActiveAntivirusCount = null };

        Assert.DoesNotContain(advisor.Advise(snapshot), r => r.Id == "antivirus.conflict");
    }

    [Fact]
    public void AntivirusDuplicado_ConNombres_RecomiendaDejarSoloDefender()
    {
        var advisor = new HardwareAdvisor(Thresholds);
        var snapshot = new SystemSnapshot
        {
            ActiveAntivirusCount = 2,
            ActiveAntivirusNames = new[] { "Windows Defender", "Avast Free Antivirus" },
        };

        Recommendation rec = Assert.Single(advisor.Advise(snapshot), r => r.Id == "antivirus.conflict");

        // La recomendación nombra al de terceros y la condición de la licencia: sin licencia de
        // pago vigente, queda solo Defender. La licencia no se puede medir, así que se pregunta.
        Assert.Contains("Avast Free Antivirus", rec.Detail, StringComparison.Ordinal);
        Assert.Contains("Defender", rec.Detail, StringComparison.Ordinal);
        Assert.Contains("licencia", rec.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AntivirusDuplicado_SinNombres_IgualRecomiendaDejarUno()
    {
        var advisor = new HardwareAdvisor(Thresholds);
        var snapshot = new SystemSnapshot { ActiveAntivirusCount = 3 };

        Recommendation rec = Assert.Single(advisor.Advise(snapshot), r => r.Id == "antivirus.conflict");
        Assert.Contains("Defender", rec.Detail, StringComparison.Ordinal);
    }

    // ---- Disco lento: sin tipo identificado no se afirma «lento para ser un SSD» -------------

    [Fact]
    public void DiscoLento_ConMediaUnknown_NoGeneraElHallazgoDeSsdLento()
    {
        var advisor = new HardwareAdvisor(Thresholds);
        var snapshot = new SystemSnapshot
        {
            PrimaryDiskMedia = DiskMedia.Unknown,
            DiskLatencyMs = 80,
        };

        Assert.DoesNotContain(advisor.Advise(snapshot), r => r.Id == "disk.slow");
    }

    // ---- Los avisos del reporte dicen cuándo no se pudo medir --------------------------------

    [Fact]
    public void BitLockerSinMedir_GeneraAvisoDeBloqueoPreventivo()
    {
        var findings = SoftwareFindings.Evaluate(new SystemSnapshot { BitLockerActive = null }, Thresholds);

        Finding finding = Assert.Single(findings, f => f.CheckId == "bitlocker.unknown");
        Assert.Contains("precaución", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BitLockerMedidoApagado_NoGeneraNingunAviso()
    {
        var findings = SoftwareFindings.Evaluate(
            new SystemSnapshot { BitLockerActive = false, IsDomainJoined = false }, Thresholds);

        Assert.DoesNotContain(findings, f => f.CheckId.StartsWith("bitlocker", StringComparison.Ordinal));
    }

    [Fact]
    public void DominioSinMedir_GeneraAvisoDeModoRestringido()
    {
        var findings = SoftwareFindings.Evaluate(new SystemSnapshot { IsDomainJoined = null }, Thresholds);

        Assert.Single(findings, f => f.CheckId == "domain.unknown");
    }

    // ---- El log distingue «no medido» de false/cero ------------------------------------------

    [Fact]
    public void LogSummary_CamposSinMedir_DicenNoMedido()
    {
        string log = new SystemSnapshot().ToLogSummary();

        Assert.Contains("bitlocker             = no medido", log, StringComparison.Ordinal);
        Assert.Contains("dominio               = no medido", log, StringComparison.Ordinal);
        Assert.Contains("impresoras            = no medido", log, StringComparison.Ordinal);
        Assert.Contains("antivirus_activos     = no medido", log, StringComparison.Ordinal);
        Assert.Contains("bateria.presente      = no medido", log, StringComparison.Ordinal);
        Assert.Contains("disco.smart_falla     = no medido", log, StringComparison.Ordinal);
    }

    [Fact]
    public void LogSummary_CamposMedidos_MuestranElValor()
    {
        string log = new SystemSnapshot
        {
            BitLockerActive = false,
            ActiveAntivirusCount = 1,
            ActiveAntivirusNames = new[] { "Windows Defender" },
            HasBattery = false,
            SmartPredictsFailure = false,
        }.ToLogSummary();

        Assert.Contains("bitlocker             = False", log, StringComparison.Ordinal);
        Assert.Contains("antivirus_activos     = 1", log, StringComparison.Ordinal);
        Assert.Contains("antivirus_nombres     = Windows Defender", log, StringComparison.Ordinal);
        Assert.Contains("disco.smart_falla     = False", log, StringComparison.Ordinal);
    }
}
