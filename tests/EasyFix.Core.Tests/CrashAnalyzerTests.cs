using EasyFix.Core.Diagnostics;
using Xunit;

namespace EasyFix.Core.Tests;

public sealed class BugCheckCatalogTests
{
    [Theory]
    [InlineData("0x0000007e", "0x0000007e")]
    [InlineData("0x7E", "0x0000007e")]        // corto y en mayúscula
    [InlineData("7e", "0x0000007e")]          // sin prefijo
    [InlineData("0x00000050", "0x00000050")]
    [InlineData("0X124", "0x00000124")]
    public void Normaliza_LasVariantesDelMismoCodigo(string input, string expected) =>
        Assert.Equal(expected, BugCheckCatalog.Normalize(input));

    [Theory]
    [InlineData("0x50", CrashSuspect.Memory)]
    [InlineData("0x1a", CrashSuspect.Memory)]
    [InlineData("0x109", CrashSuspect.Memory)]
    [InlineData("0x139", CrashSuspect.Memory)]
    [InlineData("0x7a", CrashSuspect.Disk)]
    [InlineData("0xf4", CrashSuspect.Disk)]
    [InlineData("0x24", CrashSuspect.Disk)]
    [InlineData("0x3b", CrashSuspect.Driver)]
    [InlineData("0xd1", CrashSuspect.Driver)]
    [InlineData("0x133", CrashSuspect.Driver)]
    [InlineData("0x116", CrashSuspect.GraphicsDriver)]
    [InlineData("0x124", CrashSuspect.Hardware)]
    [InlineData("0xef", CrashSuspect.SystemFiles)]
    public void AsignaElPrimerSospechosoCorrecto(string code, CrashSuspect expected) =>
        Assert.Equal(expected, BugCheckCatalog.Lookup(code).Suspect);

    [Fact]
    public void CodigoNoCatalogado_DevuelveDesconocidoSinReventar()
    {
        BugCheck result = BugCheckCatalog.Lookup("0xdeadbeef");

        Assert.Equal(CrashSuspect.Unknown, result.Suspect);
        Assert.Contains("volcado de memoria", result.Meaning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinCodigo_NoRevienta(string? code)
    {
        BugCheck result = BugCheckCatalog.Lookup(code);
        Assert.Equal(CrashSuspect.Unknown, result.Suspect);
    }

    [Fact]
    public void TodoCodigoCatalogadoTieneNombreYExplicacion() =>
        Assert.All(
            new[] { "0xa", "0x1a", "0x1e", "0x24", "0x3b", "0x50", "0x7a", "0x7e", "0x7f", "0x9f",
                    "0xbe", "0xc2", "0xc4", "0xd1", "0xef", "0xf4", "0xf7", "0x109", "0x116",
                    "0x124", "0x133", "0x139", "0x13a" },
            code =>
            {
                BugCheck bc = BugCheckCatalog.Lookup(code);
                Assert.NotEqual("NO CATALOGADO", bc.Name);
                Assert.False(string.IsNullOrWhiteSpace(bc.Meaning));
                Assert.NotEqual(CrashSuspect.Unknown, bc.Suspect);
            });
}

public sealed class CrashAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static CrashEvent Crash(string code, int daysAgo) =>
        new(Now.AddDays(-daysAgo), code);

    private static InstalledUpdate Update(string id, int daysAgo) =>
        new(id, Now.AddDays(-daysAgo));

    private static CrashData Data(
        IEnumerable<CrashEvent>? crashes = null,
        int minidumps = 0,
        int whea = 0,
        int shutdowns = 0,
        int diskErrors = 0,
        IEnumerable<InstalledUpdate>? updates = null,
        IEnumerable<string>? problemDevices = null) =>
        new(
            (crashes ?? Array.Empty<CrashEvent>()).ToList(),
            minidumps, whea, shutdowns, diskErrors,
            (updates ?? Array.Empty<InstalledUpdate>()).ToList(),
            (problemDevices ?? Array.Empty<string>()).ToList(),
            60);

    // ---- El sospechoso dominante -----------------------------------------------------------

    [Fact]
    public void ElSospechosoDominanteEsElMasFrecuente()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[]
        {
            Crash("0x50", 10),   // memoria
            Crash("0x50", 8),    // memoria
            Crash("0x3b", 5),    // driver
        }));

        Assert.Equal(CrashSuspect.Memory, result.DominantSuspect);
        Assert.Equal(2, result.SuspectCounts[CrashSuspect.Memory]);
        Assert.Equal(1, result.SuspectCounts[CrashSuspect.Driver]);
    }

    [Fact]
    public void AnteEmpate_GanaLaCategoriaMasGrave()
    {
        // En la duda conviene revisar hardware antes que software: descartar RAM cuesta una noche;
        // reinstalar Windows por un problema de RAM cuesta el doble y no arregla nada.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[]
        {
            Crash("0x3b", 10),   // driver
            Crash("0x50", 8),    // memoria
        }));

        Assert.Equal(CrashSuspect.Memory, result.DominantSuspect);
    }

    [Fact]
    public void SinPantallazos_ElSospechosoEsDesconocido()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(CrashData.Empty);

        Assert.Equal(CrashSuspect.Unknown, result.DominantSuspect);
        Assert.False(result.HasCrashes);
        Assert.Empty(CrashAnalyzer.ToFindings(result));
    }

    // ---- WHEA manda ------------------------------------------------------------------------

    [Fact]
    public void ConEventosWhea_SeConfirmaHardware()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(whea: 3));

        Assert.True(result.HardwareConfirmed);
        Assert.Contains(CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.whea");
    }

    [Fact]
    public void CodigoDeCategoriaHardware_TambienConfirmaHardware()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[] { Crash("0x124", 3) }));

        Assert.True(result.HardwareConfirmed);
    }

    [Fact]
    public void WheaTieneSeveridadCritica()
    {
        IReadOnlyList<Finding> findings = CrashAnalyzer.ToFindings(CrashAnalyzer.Analyze(Data(whea: 1)));

        Finding whea = Assert.Single(findings, f => f.CheckId == "crash.whea");
        Assert.Equal(Severity.Critical, whea.Severity);
        Assert.Contains("Ninguna reparación de software", whea.Detail, StringComparison.Ordinal);
    }

    // ---- La correlación con actualizaciones: el caso que preguntó el usuario ----------------

    [Fact]
    public void ActualizacionDentroDeLosSieteDiasPrevios_SeCorrelaciona()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 10) },
            updates: new[] { Update("KB5034441", 12) }));   // 2 días antes del pantallazo

        Assert.Single(result.CorrelatedUpdates);
        Assert.True(result.UpdateHypothesisSupported);
        Assert.Contains(CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.update-correlation");
    }

    [Fact]
    public void ActualizacionFueraDeLaVentana_NoSeCorrelaciona()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 10) },
            updates: new[] { Update("KB5034441", 30) }));   // 20 días antes: demasiado lejos

        Assert.Empty(result.CorrelatedUpdates);
        Assert.False(result.UpdateHypothesisSupported);

        // Descartar la hipótesis también es información, y ahorra horas de trabajo inútil.
        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.no-update-correlation");
        Assert.Contains("no se sostiene", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ActualizacionPosteriorAlPantallazo_NoSeCorrelaciona()
    {
        // Una actualización instalada DESPUÉS no pudo causar un pantallazo anterior.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 20) },
            updates: new[] { Update("KB5034441", 5) }));

        Assert.Empty(result.CorrelatedUpdates);
    }

    [Fact]
    public void ConHardwareConfirmado_LaHipotesisDeActualizacionNoSeSostiene()
    {
        // Aunque las fechas coincidan: si el hardware falla, la actualización solo lo destapó.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x124", 10) },
            updates: new[] { Update("KB5034441", 12) },
            whea: 2));

        Assert.Single(result.CorrelatedUpdates);
        Assert.True(result.HardwareConfirmed);
        Assert.False(result.UpdateHypothesisSupported);

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.update-correlation");
        Assert.Contains("destapado", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CuandoElCodigoApuntaAHardware_AvisaAntesDeDesinstalarLaActualizacion()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x50", 10) },          // memoria
            updates: new[] { Update("KB5034441", 12) }));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.update-correlation");

        Assert.Contains("la memoria RAM", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("antes de desinstalar", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SinActualizacionesEnLaVentana_NoSeEmiteNingunHallazgoDeCorrelacion()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(crashes: new[] { Crash("0x3b", 10) }));

        Assert.DoesNotContain(CrashAnalyzer.ToFindings(result),
            f => f.CheckId.StartsWith("crash.update", StringComparison.Ordinal) ||
                 f.CheckId.StartsWith("crash.no-update", StringComparison.Ordinal));
    }

    // ---- El primer pantallazo --------------------------------------------------------------

    [Fact]
    public void ElPrimerPantallazoEsElMasAntiguo()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[]
        {
            Crash("0x3b", 5),
            Crash("0x3b", 30),   // el más antiguo
            Crash("0x3b", 12),
        }));

        Assert.Equal(Now.AddDays(-30), result.FirstCrash!.When);
    }

    // ---- Señales de apoyo ------------------------------------------------------------------

    [Fact]
    public void ErroresDeDisco_SonCriticos_YSugierenChkdsk()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x7a", 3) }, diskErrors: 12));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.disk-errors");

        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("disk.chkdsk", finding.Fixes);
    }

    [Fact]
    public void MasApagonesQuePantallazos_ApuntaAFuenteCalorOMemoria()
    {
        // El equipo se apaga sin llegar a mostrar el error: eso no es software.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 5) }, shutdowns: 9));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.unexpected-shutdowns");

        Assert.Contains("fuente de alimentación", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MenosApagonesQuePantallazos_NoEsUnHallazgo()
    {
        // Cada pantallazo produce su propio apagón sucio: es lo esperable, no una señal extra.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 5), Crash("0x3b", 6), Crash("0x3b", 7) },
            shutdowns: 3));

        Assert.DoesNotContain(CrashAnalyzer.ToFindings(result),
            f => f.CheckId == "crash.unexpected-shutdowns");
    }

    [Fact]
    public void DispositivosConProblema_SeReportanConSusNombres()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 5) },
            problemDevices: new[] { "Realtek PCIe GbE Family Controller", "Base System Device" }));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.problem-devices");

        Assert.Contains("Realtek", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SoloVolcadosSinEventos_SeReportaComoRegistroLimpiado()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(minidumps: 5));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.dumps-only");

        Assert.Equal(Severity.Warning, finding.Severity);
    }

    // ---- El consejo cambia según el sospechoso ---------------------------------------------

    [Theory]
    [InlineData("0x50", "reemplazarla")]                      // memoria: no se arregla con software
    [InlineData("0xef", "Esto sí se repara")]                 // archivos de sistema: sí se repara
    [InlineData("0x124", "Ninguna reparación de software")]   // hardware: no hay nada que hacer
    [InlineData("0x116", "driver de video")]
    public void ElConsejoCorrespondeAlSospechoso(string code, string expectedFragment)
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[] { Crash(code, 3) }));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.bluescreen");

        Assert.Contains(expectedFragment, finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SospechaDeMemoria_SugiereLaPruebaDeMemoria()
    {
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[] { Crash("0x50", 3) }));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.bluescreen");

        Assert.Contains("memory.schedule-test", finding.Fixes);
    }

    [Fact]
    public void SospechaDeHardware_NoSugiereNingunFix()
    {
        // No ofrecer un botón cuando no hay nada que el software pueda hacer.
        CrashAnalysis result = CrashAnalyzer.Analyze(Data(new[] { Crash("0x124", 3) }));

        Finding finding = Assert.Single(
            CrashAnalyzer.ToFindings(result), f => f.CheckId == "crash.bluescreen");

        Assert.Empty(finding.Fixes);
    }

    [Fact]
    public void LosHallazgosVanDeMasGraveAMenos()
    {
        IReadOnlyList<Finding> findings = CrashAnalyzer.ToFindings(CrashAnalyzer.Analyze(Data(
            crashes: new[] { Crash("0x3b", 5) },
            whea: 1,
            shutdowns: 9,
            diskErrors: 3,
            updates: new[] { Update("KB1", 6) },
            problemDevices: new[] { "Algo" })));

        Assert.Equal(
            findings.Select(f => f.Severity).OrderByDescending(x => x),
            findings.Select(f => f.Severity));
    }
}
