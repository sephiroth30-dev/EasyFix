using EasyFix.Core.Drivers;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// El clasificador es una lista BLANCA: solo lo que matchea una clase de bajo impacto conocida
/// sale "Safe". Todo lo demás, incluido lo que no se pudo interpretar, pide confirmación. Estos
/// tests cuidan esa asimetría — un falso positivo acá (algo riesgoso marcado "Safe") es la clase
/// de bug que instalaría el driver equivocado solo.
/// </summary>
public class DriverRiskClassifierTests
{
    [Theory]
    [InlineData("Intel Corporation - Net - 23.100.0.3")]
    [InlineData("NVIDIA - Display - 31.0.15.5212")]
    [InlineData("Realtek - Media - 6.0.9403.1")]
    [InlineData("Generic - USB - 10.0.19041.1")]
    [InlineData("Qualcomm Atheros Communications - Bluetooth - 12.0.0.560")]
    [InlineData("HP Inc. - Printer - 8.1.0.0")]
    [InlineData("Microsoft - Keyboard - 1.0.0.1")]
    [InlineData("Logitech - Mouse - 1.0.0.1")]
    [InlineData("Logitech - Camera - 2.3.4.5")]
    public void ClasesDeBajoImpacto_SonSafe(string title) =>
        Assert.Equal(DriverRiskClass.Safe, DriverRiskClassifier.Classify(title));

    [Theory]
    [InlineData("Intel Corporation - SCSIAdapter - 17.2.1.1030")]
    [InlineData("LSI - RAID Controller - 6.1.3.33")]
    [InlineData("Samsung - Storage - 2.1.0.0")]
    [InlineData("Marvell - HDC - 1.0.3.0")]
    public void ClasesDeAlmacenamiento_PidenConfirmacion(string title) =>
        Assert.Equal(DriverRiskClass.NeedsConfirmation, DriverRiskClassifier.Classify(title));

    [Theory]
    [InlineData("Dell Inc. - Firmware - 1.2.3.4")]
    [InlineData("Lenovo - BIOS - 2.14.0.0")]
    [InlineData("HP - UEFI - 1.0.0.1")]
    [InlineData("Dell firmware update 1.2.3")] // sin el formato "a - b - c", igual tiene que cortar
    public void FirmwareOBios_NuncaEsSafe_YSeDistingueDeNeedsConfirmation(string title) =>
        Assert.Equal(DriverRiskClass.Firmware, DriverRiskClassifier.Classify(title));

    [Fact]
    public void FirmwareGanaAunqueElTokenDeClaseParezcaSeguro()
    {
        // Caso adversarial: un título que matchearía "Net" como clase, pero menciona firmware en
        // otra parte. Firmware tiene que ganar siempre — es la condición de mayor daño posible.
        DriverRiskClass result = DriverRiskClassifier.Classify(
            "Contoso - Net - firmware-bundled-1.0");

        Assert.Equal(DriverRiskClass.Firmware, result);
    }

    [Theory]
    [InlineData("Un título sin el formato esperado")]
    [InlineData("Fabricante - ClaseInventada - 1.0")]
    [InlineData("")]
    public void TituloSinClaseReconocida_PideConfirmacion_NuncaSafePorDescarte(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            Assert.Throws<ArgumentException>(() => DriverRiskClassifier.Classify(title));
            return;
        }

        Assert.Equal(DriverRiskClass.NeedsConfirmation, DriverRiskClassifier.Classify(title));
    }

    [Fact]
    public void LasCategoriasTambienSeRevisanParaFirmware()
    {
        // El título por sí solo no dice nada ("Contoso - Net - 1.0" matchearía Safe), pero la
        // categoría que trae Windows Update sí menciona firmware — tiene que ganar igual.
        DriverRiskClass result = DriverRiskClassifier.Classify(
            "Contoso - Net - 1.0", categories: new[] { "Firmware", "Drivers" });

        Assert.Equal(DriverRiskClass.Firmware, result);
    }

    [Fact]
    public void NoDistingueMayusculas()
    {
        Assert.Equal(DriverRiskClass.Safe, DriverRiskClassifier.Classify("Vendor - NET - 1.0"));
        Assert.Equal(DriverRiskClass.Firmware, DriverRiskClassifier.Classify("Vendor - FIRMWARE - 1.0"));
    }

    [Fact]
    public void VendorConGuionesEnElNombre_NoRevientaYFallaCerrado()
    {
        // "Advanced Micro Devices - Graphics - Net - 1.0" tiene 4 partes: el token que cae en
        // parts[1] es "Graphics", no "Net". No es el resultado ideal, pero tampoco es Safe por
        // error — sigue cayendo del lado seguro.
        DriverRiskClass result = DriverRiskClassifier.Classify(
            "Advanced Micro Devices - Graphics - Net - 1.0");

        Assert.NotEqual(DriverRiskClass.Safe, result);
    }
}
