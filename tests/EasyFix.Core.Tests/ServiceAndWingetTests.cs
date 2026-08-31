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
    public void CodigoDeYaInstalado_ConfirmadoPorLaSalida_NoEsUnFallo()
    {
        // El código coincide Y winget lo dice: hay evidencia, cuenta como éxito.
        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip",
            Result(WingetErrorCodes.PackageAlreadyInstalled, "7-Zip is already installed."));

        Assert.Equal(WingetOutcome.AlreadyInstalled, result.Outcome);
        Assert.True(result.PackageAvailable);
    }

    [Fact]
    public void CodigoDeYaInstalado_SinNadaQueLoConfirme_SeReportaComoFallo()
    {
        // ESTE ES EL TEST QUE IMPORTA. La constante 0x8A150056 está transcrita de memoria y nunca se
        // vio en un log. Si está mal, apunta a algún error real de winget — y como «ya estaba
        // instalado» cuenta como paquete disponible, EasyFix reportaría un ÉXITO que no ocurrió.
        //
        // Un éxito falso no deja rastro y no se puede diagnosticar después. Un fallo falso se ve y se
        // corrige. Sin confirmación, se elige el fallo.
        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip", Result(WingetErrorCodes.PackageAlreadyInstalled));

        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.False(result.PackageAvailable);

        // Y se dice por qué, para que el técnico pueda comprobarlo a mano.
        Assert.Contains("no se pudo confirmar", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaTablaDelEquipoManda_SobreNuestrasConstantes()
    {
        // Con la tabla cargada, el símbolo lo provee winget y las constantes dejan de importar: un
        // código que no está en ninguna constante nuestra igual se reconoce bien.
        WingetErrorTable table = WingetErrorTable.Parse(
            "0x8A15002B  APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE  No applicable update found\n" +
            "0x8A150099  APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED  Already installed\n");

        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip", Result(unchecked((int)0x8A150099)), table);

        Assert.Equal(WingetOutcome.AlreadyInstalled, result.Outcome);
    }

    [Fact]
    public void LaTablaDelEquipoTambienPuedeDesmentirUnaConstanteNuestra()
    {
        // El caso inverso y el más valioso: la tabla dice que 0x8A150056 es otra cosa. Se le cree a
        // winget, no a la constante, y se reporta el fallo.
        WingetErrorTable table = WingetErrorTable.Parse(
            "0x8A150056  APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE  El paquete está en uso\n");

        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip",
            Result(WingetErrorCodes.PackageAlreadyInstalled, "7-Zip is already installed."),
            table);

        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.Contains("INSTALL_PACKAGE_IN_USE", result.Detail);
    }

    [Fact]
    public void PaqueteNoEncontrado_LoDiceYApuntaAlAppSettings()
    {
        // 0x8A150014 verificado en la prueba real: RustDesk, removido del catálogo de winget.
        WingetResult result = WingetResultParser.Parse(
            "Vendor.Renamed", Result(WingetErrorCodes.NoApplicationsFound));

        Assert.Equal(WingetOutcome.NotInCatalog, result.Outcome);
        Assert.False(result.PackageAvailable);
        Assert.Contains("appsettings.json", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateNotApplicable_EsYaInstalado_NoUnFallo()
    {
        // El bug más costoso de la primera prueba: 0x8A15002B sobre un install significa que el
        // paquete ya está en su última versión. Se reportaba como "no encontrado", así que 7-Zip y
        // Visual C++ Redistributable —que la propia app había instalado dos horas antes— aparecían
        // en rojo.
        //
        // Es el stdout real que devuelve winget en ese caso. Hace falta porque ahora «ya estaba
        // instalado» exige confirmación: es el único desenlace que cuenta un código de error como
        // éxito, y una constante equivocada ahí produciría un éxito falso.
        WingetResult result = WingetResultParser.Parse(
            "7zip.7zip",
            Result(WingetErrorCodes.UpdateNotApplicable,
                "No newer package versions are available from the configured sources."));

        Assert.Equal(WingetOutcome.AlreadyInstalled, result.Outcome);
        Assert.True(result.PackageAvailable);
    }

    [Theory]
    [InlineData(0x8A15000F)]   // SOURCE_DATA_MISSING: el caso de la sesión elevada
    [InlineData(0x8A150019)]   // FAILED_TO_OPEN_ALL_SOURCES
    public void ProblemasDeCatalogo_SeDistinguenYExplicanLaCausa(long code)
    {
        WingetResult result = WingetResultParser.Parse("Any.Package", Result(unchecked((int)code)));

        Assert.Equal(WingetOutcome.SourceUnavailable, result.Outcome);
        Assert.Contains("administrador", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ConLaTablaDeWinget_ElDetalleTraeElSimboloOficial()
    {
        // Cargar la tabla del winget instalado hace que el detalle no dependa de constantes nuestras.
        WingetErrorTable table = WingetErrorTable.Parse(
            "0x8A150099  APPINSTALLER_CLI_ERROR_SOMETHING_ODD  Algo raro pasó");

        WingetResult result = WingetResultParser.Parse(
            "X.Y", Result(unchecked((int)0x8A150099)), table);

        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.Contains("APPINSTALLER_CLI_ERROR_SOMETHING_ODD", result.Detail, StringComparison.Ordinal);
        Assert.Contains("Algo raro pasó", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TablaDeWinget_ToleraCambiosDeFormato()
    {
        // El formato de "winget error --output" puede cambiar entre versiones: se busca el hexadecimal
        // y el símbolo en cualquier posición, en vez de asumir columnas.
        WingetErrorTable table = WingetErrorTable.Parse(
            "| APPINSTALLER_CLI_ERROR_TEST | 0x8A150001 | Descripción al final |\n" +
            "basura sin código\n" +
            "0x8A150002   APPINSTALLER_CLI_ERROR_OTRO   Otra cosa");

        Assert.Equal(2, table.Count);
        Assert.Equal("APPINSTALLER_CLI_ERROR_OTRO", table.SymbolFor(unchecked((int)0x8A150002)));
    }

    [Fact]
    public void SinInstaladorCompatible_SeDistingueDeUnFalloGenerico()
    {
        WingetResult result = WingetResultParser.Parse(
            "Some.Package", Result(WingetErrorCodes.NoApplicableInstaller));

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
    public void HashMismatch_ExplicaQueEsTransitorioYSeReintenta()
    {
        // 0x8A150011 verificado en un equipo real: Chrome falló con este código. Es una descarga
        // dañada, así que el servicio lo reintenta una vez antes de darlo por perdido.
        WingetResult result = WingetResultParser.Parse(
            "Google.Chrome", Result(WingetErrorCodes.InstallerHashMismatch));

        Assert.Equal(WingetOutcome.Failed, result.Outcome);
        Assert.Contains("descargó dañado", result.Detail, StringComparison.Ordinal);
        Assert.True(WingetErrorCodes.IsWorthRetrying(WingetErrorCodes.InstallerHashMismatch));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("  \\  ")]
    [InlineData("|")]
    [InlineData("████▒▒░░")]
    [InlineData("...")]
    public void LaAnimacionDeProgresoNoSeUsaComoMensajeDeError(string noise)
    {
        // En un equipo real el detalle de un fallo terminó siendo literalmente "-", porque winget
        // dibuja un spinner en stdout y se tomaba como la primera línea útil.
        WingetResult result = WingetResultParser.Parse("X.Y", Result(1, stdout: noise));

        Assert.DoesNotContain(noise.Trim(), result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnMensajeRealConservaSuPuntuacion()
    {
        // El filtro de animación no debe recortar contenido: "Access is denied." conserva su punto.
        WingetResult result = WingetResultParser.Parse(
            "X.Y", Result(1, stderr: "Access is denied."));

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
            WingetResultParser.Parse("C",
                Result(WingetErrorCodes.PackageAlreadyInstalled, "C is already installed.")),
            WingetResultParser.Parse("D", Result(WingetErrorCodes.NoApplicationsFound)),
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
