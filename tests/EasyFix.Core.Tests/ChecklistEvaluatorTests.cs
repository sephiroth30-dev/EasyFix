using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Network;
using EasyFix.Core.Verification;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// La lista de verificación es el entregable que convierte «te lo dejé sin errores» en algo
/// verificable. Estos tests cuidan sus dos invariantes: un dato sin medir jamás sale verde, y cada
/// estado lleva el número medido en el motivo.
/// </summary>
public class ChecklistEvaluatorTests
{
    private static readonly ThresholdOptions Thresholds = new();
    private static readonly CheckFailure[] NoFailures = Array.Empty<CheckFailure>();

    private static readonly SystemSnapshot HealthyMeasured = new()
    {
        PrimaryDiskMedia = DiskMedia.Ssd,
        PrimaryDiskHealth = DiskHealth.Healthy,
        SystemDriveFreePercent = 40,
        CommitUsedPercent = 50,
        ActiveAntivirusCount = 1,
        ActiveAntivirusNames = new[] { "Windows Defender" },
        StartupDegradationMs = 4_000,
        MainPathBootTimeMs = 25_000,
        Network = ConnectivityStatus.Online,
    };

    private static ChecklistReport Evaluate(
        SystemSnapshot snapshot,
        CrashData? crash = null,
        IReadOnlyList<CheckFailure>? failures = null,
        DeepScanResults? deep = null) =>
        ChecklistEvaluator.Evaluate(snapshot, crash ?? CrashData.Empty, failures ?? NoFailures, Thresholds, deep);

    // ---- Estructura ---------------------------------------------------------------------------

    [Fact]
    public void SiempreEvaluaLasDoceCondiciones()
    {
        ChecklistReport report = Evaluate(new SystemSnapshot());

        Assert.Equal(12, report.Items.Count);
        Assert.Equal(report.Items.Count, report.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void TodoItemLlevaMotivo()
    {
        // Un estado sin motivo es una opinión; la regla del proyecto exige el dato medido al lado.
        foreach (ChecklistItem item in Evaluate(HealthyMeasured).Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Reason), $"{item.Id} sin motivo");
        }
    }

    // ---- «No medido» jamás sale verde ---------------------------------------------------------

    [Fact]
    public void SnapshotVacio_NadaSaleVerde()
    {
        // Un snapshot donde no se midió nada no puede declarar ni una condición sana.
        ChecklistReport report = Evaluate(new SystemSnapshot());

        Assert.DoesNotContain(report.Items.Where(i => i.Id != "devices.ok" && i.Id != "crashes.none" && i.Id != "disk.events"),
            i => i.Status == ChecklistStatus.Pass);
    }

    [Fact]
    public void SondaCrashCaida_LosItemsDeEventosQuedanNoDeterminados()
    {
        // CrashData.Empty con la sonda caída NO es «cero pantallazos»: es que nadie miró. Era la
        // sospecha #2 del plan — concluir ausencia de problemas desde una comprobación que no terminó.
        var failures = new[] { new CheckFailure("crash", "timeout", true) };

        ChecklistReport report = Evaluate(HealthyMeasured, CrashData.Empty, failures);

        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "crashes.none").Status);
        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "devices.ok").Status);
        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "disk.events").Status);
    }

    [Fact]
    public void EventLogIlegiblePorBloque_NoSaleVerdeAunqueLaSondaTermine()
    {
        // Hallazgo crítico de la ronda 2: ProbeCrashData traga excepciones por bloque a propósito,
        // así que la sonda «termina bien» con listas vacías. Vacío con el bloque ilegible NO es
        // «cero problemas»: los flags de lectura degradan cada condición a no determinado.
        CrashData unreadable = CrashData.Empty with
        {
            CrashLogRead = false,
            DiskEventsRead = false,
            DevicesRead = false,
        };

        ChecklistReport report = Evaluate(HealthyMeasured, unreadable, NoFailures);

        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "crashes.none").Status);
        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "disk.events").Status);
        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "devices.ok").Status);
    }

    [Fact]
    public void SoloLosEventosDeDiscoIlegibles_DegradaSoloEsaCondicion()
    {
        CrashData partial = CrashData.Empty with { DiskEventsRead = false };

        ChecklistReport report = Evaluate(HealthyMeasured, partial, NoFailures);

        Assert.Equal(ChecklistStatus.NotDetermined, Item(report, "disk.events").Status);
        Assert.Equal(ChecklistStatus.Pass, Item(report, "crashes.none").Status);
        Assert.Equal(ChecklistStatus.Pass, Item(report, "devices.ok").Status);
    }

    [Fact]
    public void BeforeAfter_RegresionYEvidenciaPerdida_NoSeDisfrazan()
    {
        // Pass→Fail es una REGRESIÓN, y Fail→NotDetermined no es una mejora: que la re-medición
        // fallara no es evidencia de que el problema se fue.
        ChecklistReport before = Evaluate(new SystemSnapshot
        {
            SystemDriveFreePercent = 40,   // bien
            ActiveAntivirusCount = 2,      // mal
        });

        ChecklistReport after = Evaluate(new SystemSnapshot
        {
            SystemDriveFreePercent = 5,    // EMPEORÓ
            ActiveAntivirusCount = null,   // ya no se pudo medir
        });

        var pair = new BeforeAfterChecklist(before, after);

        Assert.Equal(1, pair.RegressedCount);
        Assert.Equal(1, pair.LostEvidenceCount);
        Assert.Equal(0, pair.ImprovedCount);
    }

    [Fact]
    public void SmartNegativoConSaludDesconocida_NoDeclaraDiscoSano()
    {
        // La versión vieja ascendía Unknown a Healthy con SMART negativo. El checklist no repite eso.
        var snapshot = new SystemSnapshot
        {
            PrimaryDiskHealth = DiskHealth.Unknown,
            SmartPredictsFailure = false,
        };

        ChecklistItem item = Item(Evaluate(snapshot), "disk.health");

        Assert.Equal(ChecklistStatus.NotDetermined, item.Status);
        Assert.Contains("No alcanza", item.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SmartPrediceFalla_FallaAunqueElEstadoGeneralNoSeLeyera()
    {
        var snapshot = new SystemSnapshot
        {
            PrimaryDiskHealth = DiskHealth.Unknown,
            SmartPredictsFailure = true,
        };

        ChecklistItem item = Item(Evaluate(snapshot), "disk.health");

        Assert.Equal(ChecklistStatus.Fail, item.Status);
        Assert.True(item.NeedsHardware);
    }

    // ---- Equipo sano medido: todo verde salvo lo profundo -------------------------------------

    [Fact]
    public void EquipoSanoMedido_SoloQuedanPendientesLosEscaneosProfundos()
    {
        ChecklistReport report = Evaluate(HealthyMeasured);

        Assert.Equal(0, report.FailCount);

        var notDetermined = report.Items
            .Where(i => i.Status == ChecklistStatus.NotDetermined)
            .Select(i => i.Id)
            .ToList();

        Assert.Equal(new[] { "system.files", "filesystem.clean" }, notDetermined);
    }

    [Fact]
    public void ConEscaneoProfundoCorrido_LasDoceQuedanEvaluadas()
    {
        ChecklistReport report = Evaluate(
            HealthyMeasured, deep: new DeepScanResults(SystemFilesHealthy: true, FileSystemClean: true));

        Assert.Equal(12, report.PassCount);
    }

    // ---- Condiciones individuales -------------------------------------------------------------

    [Theory]
    [InlineData(40, ChecklistStatus.Pass)]
    [InlineData(10, ChecklistStatus.Fail)]
    public void EspacioLibre_ComparaContraElUmbral(double freePercent, ChecklistStatus expected)
    {
        var snapshot = new SystemSnapshot { SystemDriveFreePercent = freePercent };
        Assert.Equal(expected, Item(Evaluate(snapshot), "disk.space").Status);
    }

    [Theory]
    [InlineData(0, ChecklistStatus.Fail)]   // sin protección también es rojo
    [InlineData(1, ChecklistStatus.Pass)]
    [InlineData(2, ChecklistStatus.Fail)]
    public void Antivirus_SoloUnoEsVerde(int count, ChecklistStatus expected)
    {
        var snapshot = new SystemSnapshot { ActiveAntivirusCount = count };
        Assert.Equal(expected, Item(Evaluate(snapshot), "antivirus.single").Status);
    }

    [Fact]
    public void AntivirusDuplicado_ElMotivoNombraLosProductos()
    {
        var snapshot = new SystemSnapshot
        {
            ActiveAntivirusCount = 2,
            ActiveAntivirusNames = new[] { "Windows Defender", "McAfee LiveSafe" },
        };

        Assert.Contains("McAfee LiveSafe", Item(Evaluate(snapshot), "antivirus.single").Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetrasoDeInicioAlto_Falla()
    {
        var snapshot = new SystemSnapshot { StartupDegradationMs = 31_400 };

        ChecklistItem item = Item(Evaluate(snapshot), "startup.light");

        Assert.Equal(ChecklistStatus.Fail, item.Status);
        Assert.Contains("31,4", item.Reason, StringComparison.Ordinal); // coma: lector hispanohablante
    }

    [Fact]
    public void SinRetrasoMedido_ElConteoDecide_YLoDice()
    {
        var snapshot = new SystemSnapshot { EnabledStartupEntryCount = 14 };

        ChecklistItem item = Item(Evaluate(snapshot), "startup.light");

        Assert.Equal(ChecklistStatus.Fail, item.Status);
        Assert.Contains("no", item.Reason, StringComparison.OrdinalIgnoreCase); // dice que no midió el retraso
    }

    [Fact]
    public void ArranqueLentoEnHdd_SeMarcaComoHardware()
    {
        var snapshot = new SystemSnapshot
        {
            MainPathBootTimeMs = 120_000,
            PrimaryDiskMedia = DiskMedia.Hdd,
        };

        ChecklistItem item = Item(Evaluate(snapshot), "boot.time");

        Assert.Equal(ChecklistStatus.Fail, item.Status);
        Assert.True(item.NeedsHardware);
    }

    [Fact]
    public void ArranqueLentoConDiscoSinIdentificar_NoAfirmaQueEsHardware()
    {
        var snapshot = new SystemSnapshot { MainPathBootTimeMs = 120_000 };

        Assert.False(Item(Evaluate(snapshot), "boot.time").NeedsHardware);
    }

    // ---- Antes/después ------------------------------------------------------------------------

    [Fact]
    public void BeforeAfter_CuentaLasMejorasRealesYLasQuePersisten()
    {
        ChecklistReport before = Evaluate(new SystemSnapshot
        {
            SystemDriveFreePercent = 8,
            ActiveAntivirusCount = 2,
            PrimaryDiskHealth = DiskHealth.Failing,
        });

        ChecklistReport after = Evaluate(new SystemSnapshot
        {
            SystemDriveFreePercent = 25,        // la limpieza liberó espacio: mejora real
            ActiveAntivirusCount = 2,            // el antivirus duplicado sigue: persiste
            PrimaryDiskHealth = DiskHealth.Failing, // el disco sigue fallando: persiste (hardware)
        });

        var pair = new BeforeAfterChecklist(before, after);

        Assert.Equal(1, pair.ImprovedCount);
        Assert.Equal(2, pair.StillFailingCount);
        Assert.Equal(before.Items.Count, pair.Rows.Count);
    }

    private static ChecklistItem Item(ChecklistReport report, string id) =>
        report.Items.Single(i => i.Id == id);
}
