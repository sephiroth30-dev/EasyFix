using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Recommendations;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El motor de recomendaciones es lo que decide si el cliente gasta plata. Los tests que importan
/// son los de "no recomendar": sobre un dato que no se midió, y sobre un equipo que está bien.
/// </summary>
public sealed class HardwareAdvisorTests
{
    private static readonly ThresholdOptions Defaults = new();

    private static HardwareAdvisor Advisor() => new(Defaults);

    /// <summary>Equipo sano: SSD, 16 GB sin presión, disco con espacio, sin throttling.</summary>
    private static SystemSnapshot HealthyPc => new()
    {
        PrimaryDiskMedia = DiskMedia.Ssd,
        PrimaryDiskHealth = DiskHealth.Healthy,
        SystemDriveFreePercent = 45,
        SystemDriveTotalGb = 500,
        DiskLatencyMs = 2,
        TotalRamGb = 16,
        CommitUsedPercent = 40,
        CpuPercentOfMaxFrequency = 95,
        ActiveAntivirusCount = 1,
    };

    private static Recommendation? Find(IReadOnlyList<Recommendation> list, string id) =>
        list.FirstOrDefault(r => r.Id == id);

    // ---- Lo más importante: cuándo NO recomendar -------------------------------------------

    [Fact]
    public void EquipoSano_NoRecomiendaNada()
    {
        // Si el equipo está bien, la respuesta correcta es no vender nada.
        Assert.Empty(Advisor().Advise(HealthyPc));
    }

    [Fact]
    public void SnapshotVacio_NoRecomiendaNada()
    {
        // Todo en null significa "no se midió". Recomendar sobre datos ausentes hace que el cliente
        // gaste plata por una corazonada.
        Assert.Empty(Advisor().Advise(new SystemSnapshot()));
    }

    [Fact]
    public void SinDatoDeRam_NoRecomiendaRam()
    {
        var snapshot = HealthyPc with { TotalRamGb = null, CommitUsedPercent = 99 };

        Assert.Null(Find(Advisor().Advise(snapshot), "memory.insufficient"));
    }

    [Fact]
    public void DesktopSinBateria_NoRecomiendaNadaDeBateria()
    {
        // BatteryWearPercent null en un desktop es lo normal, no un fallo.
        Assert.Null(Find(Advisor().Advise(HealthyPc with { BatteryWearPercent = null }), "battery.worn"));
    }

    [Fact]
    public void TipoDeDiscoDesconocido_NoRecomiendaSsd()
    {
        var snapshot = HealthyPc with { PrimaryDiskMedia = DiskMedia.Unknown };

        Assert.Null(Find(Advisor().Advise(snapshot), "disk.mechanical"));
    }

    // ---- Disco fallando: bloquea todo ------------------------------------------------------

    [Fact]
    public void DiscoFallando_EsUrgente_YBloqueaLosFixes()
    {
        var snapshot = HealthyPc with { PrimaryDiskHealth = DiskHealth.Failing };
        HardwareAdvisor advisor = Advisor();

        IReadOnlyList<Recommendation> recommendations = advisor.Advise(snapshot);
        Recommendation failing = Assert.Single(recommendations, r => r.Id == "disk.failing");

        Assert.Equal(RecommendationPriority.Urgent, failing.Priority);
        Assert.True(failing.BlocksFixes);
        Assert.True(advisor.ShouldBlockFixes(snapshot));

        // Y va primero: es lo que el técnico tiene que ver antes que nada.
        Assert.Equal("disk.failing", recommendations[0].Id);
    }

    [Fact]
    public void DiscoSano_NoBloqueaLosFixes()
    {
        Assert.False(Advisor().ShouldBlockFixes(HealthyPc with { PrimaryDiskMedia = DiskMedia.Hdd }));
    }

    // ---- HDD: la mejora #1 -----------------------------------------------------------------

    [Fact]
    public void DiscoMecanico_EsLaMejoraDeMayorImpacto()
    {
        var snapshot = HealthyPc with { PrimaryDiskMedia = DiskMedia.Hdd };

        Recommendation ssd = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.mechanical");

        Assert.Equal(RecommendationPriority.TopImpact, ssd.Priority);
        Assert.Contains("mejora #1", ssd.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoMecanicoConArranqueMedido_DaElNumeroRealYUnaEstimacion()
    {
        var snapshot = HealthyPc with
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            MainPathBootTimeMs = 94_000,
        };

        Recommendation ssd = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.mechanical");

        Assert.Contains("94 s", ssd.Detail, StringComparison.Ordinal);
        Assert.Contains("23,5 s", ssd.Detail, StringComparison.Ordinal);   // 94 / 4
        Assert.NotNull(ssd.Evidence);
        Assert.Equal("s", ssd.Evidence!.Unit);
    }

    [Fact]
    public void ArranqueYaRapido_LaEstimacionNoBajaDelPiso()
    {
        // Una regla de tres daría "2 s", que es absurdo y destruye la credibilidad del reporte.
        var snapshot = HealthyPc with
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            MainPathBootTimeMs = 8_000,
        };

        Recommendation ssd = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.mechanical");

        Assert.Contains("15 s", ssd.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoMecanicoSinArranqueMedido_NoInventaSegundos()
    {
        var snapshot = HealthyPc with
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            MainPathBootTimeMs = null,
        };

        Recommendation ssd = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.mechanical");

        Assert.DoesNotContain("tarda", ssd.Detail, StringComparison.Ordinal);
        Assert.Null(ssd.Evidence);
    }

    [Theory]
    [InlineData(120, "240")]
    [InlineData(240, "240")]
    [InlineData(500, "1000")]
    [InlineData(2000, "2000")]
    public void SugiereUnTamanoDeSsdQueNoObligaABorrarDatos(double currentGb, string expectedSize)
    {
        var snapshot = HealthyPc with
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            SystemDriveTotalGb = currentGb,
        };

        Recommendation ssd = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.mechanical");

        Assert.Contains($"{expectedSize} GB", ssd.Detail, StringComparison.Ordinal);
    }

    // ---- RAM -------------------------------------------------------------------------------

    [Fact]
    public void PocaRamConPresionDeMemoria_EsDeMayorImpactoQueSinPresion()
    {
        var withPressure = HealthyPc with { TotalRamGb = 8, CommitUsedPercent = 92 };
        var withoutPressure = HealthyPc with { TotalRamGb = 4, CommitUsedPercent = 30 };

        Recommendation a = Assert.Single(Advisor().Advise(withPressure), r => r.Id == "memory.insufficient");
        Recommendation b = Assert.Single(Advisor().Advise(withoutPressure), r => r.Id == "memory.insufficient");

        // Poca RAM sin presión es sospecha; con presión es un hecho medido.
        Assert.Equal(RecommendationPriority.TopImpact, a.Priority);
        Assert.Equal(RecommendationPriority.Recommended, b.Priority);
        Assert.Contains("usando el disco como memoria", a.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecomiendaElModuloExacto_CuandoSeConoceTipoYVelocidad()
    {
        var snapshot = HealthyPc with
        {
            TotalRamGb = 8,
            CommitUsedPercent = 92,
            MemoryType = "DDR4",
            MemorySpeedMhz = 2666,
            FreeMemorySlots = 1,
        };

        Recommendation ram = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "memory.insufficient");

        Assert.Contains("16 GB", ram.Detail, StringComparison.Ordinal);
        Assert.Contains("DDR4-2666", ram.Detail, StringComparison.Ordinal);
        Assert.Contains("1 slot(s) libre(s)", ram.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SinSlotsLibres_AvisaQueHayQueReemplazar()
    {
        var snapshot = HealthyPc with { TotalRamGb = 4, FreeMemorySlots = 0 };

        Recommendation ram = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "memory.insufficient");

        Assert.Contains("No hay slots libres", ram.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotsLibresDesconocidos_NoAfirmaNadaSobreSlots()
    {
        var snapshot = HealthyPc with { TotalRamGb = 4, FreeMemorySlots = null };

        Recommendation ram = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "memory.insufficient");

        Assert.DoesNotContain("slot", ram.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(4, "8 GB")]
    [InlineData(8, "16 GB")]
    [InlineData(16, "32 GB")]
    public void ElObjetivoDeRamEsElSiguienteEscalon(double current, string expected)
    {
        var snapshot = HealthyPc with { TotalRamGb = current, CommitUsedPercent = 92 };

        Recommendation ram = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "memory.insufficient");

        Assert.Contains(expected, ram.Detail, StringComparison.Ordinal);
    }

    // ---- Resto de las reglas ---------------------------------------------------------------

    [Fact]
    public void PocoEspacioLibre_SeRecomienda()
    {
        var snapshot = HealthyPc with { SystemDriveFreePercent = 6 };

        Recommendation space = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.low-space");

        Assert.Contains("6 %", space.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SsdConLatenciaAlta_SeReporta()
    {
        var snapshot = HealthyPc with { DiskLatencyMs = 40 };

        Recommendation slow = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "disk.slow");

        Assert.Contains("40 ms", slow.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoMecanicoConLatenciaAlta_NoDuplicaLaRecomendacion()
    {
        // Ya se recomendó el SSD; decir además "el disco responde lento" es ruido.
        var snapshot = HealthyPc with { PrimaryDiskMedia = DiskMedia.Hdd, DiskLatencyMs = 40 };

        IReadOnlyList<Recommendation> recommendations = Advisor().Advise(snapshot);

        Assert.NotNull(Find(recommendations, "disk.mechanical"));
        Assert.Null(Find(recommendations, "disk.slow"));
    }

    [Fact]
    public void BateriaDesgastada_SeReportaComoOpcional()
    {
        var snapshot = HealthyPc with { BatteryWearPercent = 45 };

        Recommendation battery = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "battery.worn");

        Assert.Equal(RecommendationPriority.Optional, battery.Priority);
        Assert.Contains("45 %", battery.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuLimitandose_SugiereLimpiezaAntesDeCambiarHardware()
    {
        var snapshot = HealthyPc with { CpuPercentOfMaxFrequency = 45, CpuName = "Intel Core i5-8250U" };

        Recommendation cpu = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "cpu.throttling");

        Assert.Contains("i5-8250U", cpu.Detail, StringComparison.Ordinal);
        Assert.Contains("pasta térmica", cpu.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DosAntivirus_EsDeAltoImpacto()
    {
        var snapshot = HealthyPc with { ActiveAntivirusCount = 2 };

        Recommendation av = Assert.Single(Advisor().Advise(snapshot), r => r.Id == "antivirus.conflict");

        Assert.Equal(RecommendationPriority.TopImpact, av.Priority);
    }

    [Fact]
    public void UnSoloAntivirus_NoEsProblema()
    {
        Assert.Null(Find(Advisor().Advise(HealthyPc with { ActiveAntivirusCount = 1 }), "antivirus.conflict"));
    }

    // ---- Orden -----------------------------------------------------------------------------

    [Fact]
    public void LasRecomendacionesVanDeMasUrgenteAMenos()
    {
        var terriblePc = new SystemSnapshot
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            PrimaryDiskHealth = DiskHealth.Failing,
            SystemDriveFreePercent = 3,
            TotalRamGb = 4,
            CommitUsedPercent = 97,
            BatteryWearPercent = 60,
            CpuPercentOfMaxFrequency = 40,
            ActiveAntivirusCount = 2,
        };

        IReadOnlyList<Recommendation> recommendations = Advisor().Advise(terriblePc);

        Assert.Equal(RecommendationPriority.Urgent, recommendations[0].Priority);
        Assert.Equal(
            recommendations.Select(r => r.Priority).OrderByDescending(p => p),
            recommendations.Select(r => r.Priority));
    }

    [Fact]
    public void TodaRecomendacionTieneTituloYDetalleNoVacios()
    {
        var terriblePc = new SystemSnapshot
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            PrimaryDiskHealth = DiskHealth.Failing,
            SystemDriveFreePercent = 3,
            TotalRamGb = 4,
            BatteryWearPercent = 60,
            CpuPercentOfMaxFrequency = 40,
            ActiveAntivirusCount = 3,
        };

        Assert.All(Advisor().Advise(terriblePc), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.False(string.IsNullOrWhiteSpace(r.Detail));
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
        });
    }
}
