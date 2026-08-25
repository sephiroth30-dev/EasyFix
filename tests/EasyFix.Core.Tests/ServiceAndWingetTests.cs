using EasyFix.Core.Abstractions;
using EasyFix.Core.Apps;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using Xunit;

namespace EasyFix.Core.Tests;

public sealed class ServiceConditionEvaluatorTests
{
    private static readonly ServiceConditionEvaluator Evaluator = new();

    private static ServiceCandidate Candidate(string name, string? onlyIf = null) =>
        new() { Name = name, Loses = "algo", OnlyIf = onlyIf };

    // ---- SysMain: el caso que motiva toda la clase -----------------------------------------

    [Fact]
    public void SysMain_EnDiscoMecanico_NoSeOfrece()
    {
        // Las listas de foros lo desactivan sin condición. En HDD, Superfetch MEJORA el rendimiento.
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Hdd, TotalRamGb = 16 };

        ServiceOffer offer = Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot);

        Assert.False(offer.Offer);
        Assert.Contains("MEJORA el rendimiento", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SysMain_ConTipoDeDiscoDesconocido_NoSeOfrece()
    {
        // Sin saber si es SSD o mecánico, desactivarlo podría empeorar el equipo. Falla cerrado.
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Unknown, TotalRamGb = 16 };

        ServiceOffer offer = Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot);

        Assert.False(offer.Offer);
        Assert.Contains("No se pudo determinar", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SysMain_EnSsdConPocaRam_NoSeOfrece()
    {
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Ssd, TotalRamGb = 4 };

        Assert.False(Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot).Offer);
    }

    [Fact]
    public void SysMain_EnSsdCon8Gb_SeOfrece()
    {
        // 8 GB nominales se reportan como 7,9x: el umbral está en 7,5 justamente por eso.
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Ssd, TotalRamGb = 7.92 };

        Assert.True(Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot).Offer);
    }

    [Fact]
    public void SysMain_SinDatoDeRam_NoSeOfrece()
    {
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Ssd, TotalRamGb = null };

        Assert.False(Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot).Offer);
    }

    [Fact]
    public void SysMain_EnDiscoOptane_SeTrataComoSsd()
    {
        var snapshot = new SystemSnapshot { PrimaryDiskMedia = DiskMedia.Scm, TotalRamGb = 16 };

        Assert.True(Evaluator.Evaluate(
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb), snapshot).Offer);
    }

    // ---- Impresora y Bluetooth -------------------------------------------------------------

    [Fact]
    public void Spooler_ConImpresoraInstalada_NoSeOfrece()
    {
        var snapshot = new SystemSnapshot { HasPrinters = true };

        ServiceOffer offer = Evaluator.Evaluate(
            Candidate("Spooler", ServiceConditionEvaluator.NoPrintersInstalled), snapshot);

        Assert.False(offer.Offer);
        Assert.Contains("impresión", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Spooler_SinImpresoras_SeOfrece() =>
        Assert.True(Evaluator.Evaluate(
            Candidate("Spooler", ServiceConditionEvaluator.NoPrintersInstalled),
            new SystemSnapshot { HasPrinters = false }).Offer);

    [Fact]
    public void Bluetooth_ConAdaptador_NoSeOfrece() =>
        Assert.False(Evaluator.Evaluate(
            Candidate("bthserv", ServiceConditionEvaluator.NoBluetoothAdapter),
            new SystemSnapshot { HasBluetoothAdapter = true }).Offer);

    [Fact]
    public void Bluetooth_SinAdaptador_SeOfrece() =>
        Assert.True(Evaluator.Evaluate(
            Candidate("bthserv", ServiceConditionEvaluator.NoBluetoothAdapter),
            new SystemSnapshot { HasBluetoothAdapter = false }).Offer);

    // ---- Sin condición y condición desconocida ---------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinCondicion_SeOfrece(string? onlyIf) =>
        Assert.True(Evaluator.Evaluate(Candidate("DiagTrack", onlyIf), new SystemSnapshot()).Offer);

    [Fact]
    public void CondicionDesconocida_NoSeOfrece_YLoDice()
    {
        // Un typo en appsettings.json tiene que resultar en "no hago nada", jamás en "lo desactivo".
        ServiceOffer offer = Evaluator.Evaluate(
            Candidate("Algo", "SiHayLunaLlena"), new SystemSnapshot());

        Assert.False(offer.Offer);
        Assert.Contains("SiHayLunaLlena", offer.Reason, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", offer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateAll_DevuelveUnaDecisionPorCandidato()
    {
        var snapshot = new SystemSnapshot
        {
            PrimaryDiskMedia = DiskMedia.Hdd,
            TotalRamGb = 16,
            HasPrinters = true,
        };

        var candidates = new[]
        {
            Candidate("DiagTrack"),
            Candidate("Spooler", ServiceConditionEvaluator.NoPrintersInstalled),
            Candidate("SysMain", ServiceConditionEvaluator.SsdAndRamAtLeast8Gb),
        };

        var results = Evaluator.EvaluateAll(candidates, snapshot);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Offer.Offer);    // sin condición
        Assert.False(results[1].Offer.Offer);   // hay impresora
        Assert.False(results[2].Offer.Offer);   // disco mecánico
    }

    /// <summary>
    /// Todas las condiciones que usa el <c>appsettings.json</c> real tienen que estar implementadas.
    /// Si alguien agrega una condición nueva al archivo y se olvida del código, el evaluador la
    /// rechazaría en silencio y el servicio nunca se ofrecería.
    /// </summary>
    [Fact]
    public void TodasLasCondicionesDelAppSettingsRealEstanImplementadas()
    {
        EasyFixOptions options = OptionsLoader.Parse(File.ReadAllText(TestPaths.RealAppSettings()));

        string[] known =
        {
            ServiceConditionEvaluator.NoPrintersInstalled,
            ServiceConditionEvaluator.NoBluetoothAdapter,
            ServiceConditionEvaluator.SsdAndRamAtLeast8Gb,
        };

        IEnumerable<string> used = options.Services.OfferToDisable
            .Select(s => s.OnlyIf)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal);

        Assert.All(used, condition => Assert.Contains(condition, known));
    }
}

public sealed class WingetResultParserTests
{
    private static ProcessResult Result(int? exitCode, string stdout = "", string stderr = "", bool timedOut = false) =>
        new(exitCode, stdout, stderr, timedOut, TimeSpan.FromSeconds(3));

    [Fact]
    public void ExitCodeCero_EsInstalado()
    {
        WingetResult result = WingetResultParser.Parse("Google.Chrome", Result(0, "Successfully installed"));

        Assert.Equal(WingetOutcome.Installed, result.Outcome);
        Assert.True(result.PackageAvailable);
    }

    [Fact]
    public void CeroConMensajeDeYaInstalado_NoSeCuentaComoInstaladoAhora()
    {
        // Reportar "instalé Chrome" cuando ya estaba es mentirle al técnico sobre lo que hizo.
        WingetResult result = WingetResultParser.Parse(
            "Google.Chrome", Result(0, "No newer package versions are available from the configured sources."));

        Assert.Equal(WingetOutcome.AlreadyInstalled, result.Outcome);
        Assert.True(result.PackageAvailable);
    }

    [Fact]
    public void CodigoDeYaInstalado_NoEsUnFallo()
    {
        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip", Result(WingetResultParser.PackageAlreadyInstalled));

        Assert.Equal(WingetOutcome.AlreadyInstalled, result.Outcome);
        Assert.True(result.PackageAvailable);
    }

    [Fact]
    public void PaqueteNoEncontrado_LoDiceYApuntaAlAppSettings()
    {
        WingetResult result = WingetResultParser.Parse(
            "Vendor.Renamed", Result(WingetResultParser.NoApplicationsFound));

        Assert.Equal(WingetOutcome.NotFound, result.Outcome);
        Assert.False(result.PackageAvailable);
        Assert.Contains("appsettings.json", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SinInstaladorCompatible_SeDistingueDeUnFalloGenerico()
    {
        WingetResult result = WingetResultParser.Parse(
            "Some.Package", Result(WingetResultParser.NoApplicableInstaller));

        Assert.Equal(WingetOutcome.NoApplicableInstaller, result.Outcome);
        Assert.Contains("arquitectura", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CodigoDesconocido_EsFallo_YMuestraElCodigoEnCrudo()
    {
        // Los códigos de winget están pendientes de verificar contra un Windows real. Este es el
        // comportamiento de respaldo: si una constante está mal, el peor caso es un fallo honesto y
        // diagnosticable, nunca un éxito falso.
        WingetResult result = WingetResultParser.Parse(
            "Some.Package", Result(unchecked((int)0x80070005), stderr: "Access is denied."));

        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.False(result.PackageAvailable);
        Assert.Contains("0x80070005", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeout_SeDistingueYAvisaQuePudoQuedarAMedias()
    {
        WingetResult result = WingetResultParser.Parse("Big.Package", Result(null, timedOut: true));

        Assert.Equal(WingetOutcome.TimedOut, result.Outcome);
        Assert.Contains("a medias", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WingetAusente_TieneSuPropioResultado()
    {
        WingetResult result = WingetResultParser.Missing("Google.Chrome");

        Assert.Equal(WingetOutcome.WingetMissing, result.Outcome);
        Assert.False(result.PackageAvailable);
        Assert.Contains("1809", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StderrMuyLargo_SeTrunca()
    {
        WingetResult result = WingetResultParser.Parse(
            "P", Result(1, stderr: new string('x', 500)));

        Assert.Contains('…', result.Detail);
        Assert.True(result.Detail.Length < 400);
    }

    [Fact]
    public void Summarize_SeparaInstaladosDeYaInstaladosYDeErrores()
    {
        var results = new[]
        {
            WingetResultParser.Parse("A", Result(0, "Successfully installed")),
            WingetResultParser.Parse("B", Result(0, "Successfully installed")),
            WingetResultParser.Parse("C", Result(WingetResultParser.PackageAlreadyInstalled)),
            WingetResultParser.Parse("D", Result(WingetResultParser.NoApplicationsFound)),
        };

        string summary = WingetResultParser.Summarize(results);

        Assert.Contains("2 instalado(s)", summary, StringComparison.Ordinal);
        Assert.Contains("1 ya estaba(n) instalado(s)", summary, StringComparison.Ordinal);
        Assert.Contains("1 con error", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summarize_SinSeleccion_LoDice() =>
        Assert.Contains("No se seleccionó", WingetResultParser.Summarize(Array.Empty<WingetResult>()),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IdVacio_Tira(string? id) =>
        // ThrowsAny y no Throws: para null, ThrowIfNullOrWhiteSpace tira ArgumentNullException, que
        // deriva de ArgumentException pero no es el mismo tipo exacto.
        Assert.ThrowsAny<ArgumentException>(() => WingetResultParser.Parse(id!, Result(0)));
}
