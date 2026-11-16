using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Recommendations;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Saturación y selección de software: ambos módulos solo hablan de lo medido, y la única
/// estimación (llenado de disco) se declara como estimación dentro del texto.
/// </summary>
public class AdvisorsTests
{
    private static readonly ThresholdOptions Thresholds = new();
    private static readonly SaturationAdvisor Saturation = new(Thresholds);
    private static readonly SoftwareSelectionAdvisor Software = new(Thresholds);

    // ---- SaturationAdvisor ----------------------------------------------------------------------

    [Fact]
    public void SinMediciones_NoPredicNada()
    {
        Assert.Empty(Saturation.Evaluate(new SystemSnapshot()));
    }

    [Theory]
    [InlineData(50)]   // holgado
    [InlineData(95)]   // ya en presión: eso es un hecho de HardwareAdvisor, no una predicción
    public void MemoriaFueraDeLaFranjaDeAlerta_NoAvisa(double commit)
    {
        var snapshot = new SystemSnapshot { CommitUsedPercent = commit };
        Assert.DoesNotContain(Saturation.Evaluate(snapshot), f => f.CheckId == "saturation.memory");
    }

    [Fact]
    public void MemoriaEnLaFranjaDeAlerta_AvisaConElNumeroMedido()
    {
        var snapshot = new SystemSnapshot { CommitUsedPercent = 78, TotalRamGb = 8 };

        Finding finding = Assert.Single(
            Saturation.Evaluate(snapshot), f => f.CheckId == "saturation.memory");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("78", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoEnFranjaDeProyeccion_DeclaraQueEsUnaEstimacionYElSupuesto()
    {
        // 500 GB con 20 % libre = 100 GB... eso queda fuera (>=40 GB y >=25 %? no: 20 % < 25 %).
        // 100 GB libres no dispara por el piso de 40 GB → usar un disco chico.
        var snapshot = new SystemSnapshot
        {
            SystemDriveTotalGb = 120,
            SystemDriveFreePercent = 20, // 24 GB libres: < 40 GB y > LowDiskFreePercent (15)
        };

        Finding finding = Assert.Single(
            Saturation.Evaluate(snapshot), f => f.CheckId == "saturation.disk");

        Assert.Contains("estimación", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GB por mes", finding.Detail, StringComparison.Ordinal); // el supuesto, visible
        Assert.Contains(finding.AllMetrics, m => m.Unit == "meses");
    }

    [Fact]
    public void DiscoYaPorDebajoDelUmbral_NoProyecta_EsUnProblemaDeHoy()
    {
        var snapshot = new SystemSnapshot
        {
            SystemDriveTotalGb = 120,
            SystemDriveFreePercent = 8, // ya está mal HOY: lo reporta HardwareAdvisor
        };

        Assert.DoesNotContain(Saturation.Evaluate(snapshot), f => f.CheckId == "saturation.disk");
    }

    [Fact]
    public void DiscoHolgado_NoProyecta()
    {
        var snapshot = new SystemSnapshot
        {
            SystemDriveTotalGb = 1000,
            SystemDriveFreePercent = 50,
        };

        Assert.Empty(Saturation.Evaluate(snapshot));
    }

    // ---- SoftwareSelectionAdvisor ---------------------------------------------------------------

    [Fact]
    public void SinMediciones_NoRecomiendaSoftware()
    {
        Assert.Empty(Software.Advise(new SystemSnapshot()));
    }

    [Fact]
    public void PocaRam_RecomiendaPerfilLiviano()
    {
        var recs = Software.Advise(new SystemSnapshot { TotalRamGb = 4 });

        Recommendation rec = Assert.Single(recs, r => r.Id == "software.low-ram");
        Assert.Contains("Defender", rec.Detail, StringComparison.Ordinal);
        Assert.False(rec.NeedsHardware);
    }

    [Fact]
    public void DiscoMecanicoMedido_RecomiendaEvitarResidentes()
    {
        var recs = Software.Advise(new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Hdd });

        Assert.Single(recs, r => r.Id == "software.hdd");
    }

    [Fact]
    public void DiscoSinIdentificar_NoAfirmaPerfilDeHdd()
    {
        // IsSsd == null no es un HDD: no se recomienda como si lo fuera.
        var recs = Software.Advise(new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Unknown });

        Assert.DoesNotContain(recs, r => r.Id == "software.hdd");
    }

    [Fact]
    public void ConBateriaMedida_RecomiendaPerfilLaptop()
    {
        var recs = Software.Advise(new SystemSnapshot { HasBattery = true });

        Assert.Single(recs, r => r.Id == "software.laptop");
    }

    [Fact]
    public void SinSaberSiHayBateria_NoAfirmaQueEsLaptop()
    {
        Assert.DoesNotContain(
            Software.Advise(new SystemSnapshot { HasBattery = null }),
            r => r.Id == "software.laptop");
    }

    [Fact]
    public void EquipoPotenteYMedido_NoMolestaConConsejos()
    {
        var snapshot = new SystemSnapshot
        {
            TotalRamGb = 16,
            PrimaryDiskMedia = DiskMedia.Ssd,
            HasBattery = false,
        };

        Assert.Empty(Software.Advise(snapshot));
    }
}
