using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests del cálculo del porcentaje.
/// </summary>
/// <remarks>
/// Una barra que salta de golpe entre pasos parece trabada; una que se queda quieta parece colgada.
/// Las dos hacen que el técnico cierre la aplicación a mitad de una reparación, que es el peor
/// momento posible.
/// </remarks>
public sealed class FixProgressTests
{
    [Fact]
    public void PrimerPasoSinFraccion_MuestraLaMitadDelPaso()
    {
        // Sin fracción se asume la mitad: la barra arranca moviéndose en vez de quedarse en cero.
        var progress = new FixProgress(1, 4, "Preparando");

        Assert.Equal(12.5, progress.Percent, precision: 1);
    }

    [Fact]
    public void PasoCompletado_CuentaEntero()
    {
        var progress = new FixProgress(1, 4, "Preparando", Fraction: 1);

        Assert.Equal(25, progress.Percent, precision: 1);
    }

    [Fact]
    public void UltimoPasoCompleto_LlegaACien()
    {
        var progress = new FixProgress(4, 4, "Listo", Fraction: 1);

        Assert.Equal(100, progress.Percent, precision: 1);
    }

    [Fact]
    public void ElPorcentajeAvanzaMonotonamenteEntrePasos()
    {
        // Que nunca retroceda es lo que hace que la barra se sienta confiable.
        double previous = -1;

        for (int step = 1; step <= 5; step++)
        {
            foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
            {
                double current = new FixProgress(step, 5, "x", Fraction: fraction).Percent;

                Assert.True(current >= previous,
                    $"Retrocedió en el paso {step} con fracción {fraction}: {current} < {previous}");

                previous = current;
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SinPasos_DevuelveCeroSinDividirPorCero(int total) =>
        Assert.Equal(0, new FixProgress(1, total, "x").Percent);

    [Fact]
    public void UnPasoFueraDeRango_NoSeSaleDeCeroCien()
    {
        // Defensa contra un contador mal llevado: la barra nunca debe mostrar 140 % ni negativo.
        Assert.Equal(100, new FixProgress(99, 4, "x", Fraction: 1).Percent);
        Assert.Equal(0, new FixProgress(0, 4, "x", Fraction: 0).Percent);
    }

    [Fact]
    public void UnaCorridaTipicaRecorreTodoElRango()
    {
        // 1 paso de preparación + 4 fixes.
        const int Total = 5;

        double inicio = new FixProgress(1, Total, "Preparando", Fraction: 0).Percent;
        double fin = new FixProgress(Total, Total, "Listo", Fraction: 1).Percent;

        Assert.Equal(0, inicio, precision: 1);
        Assert.Equal(100, fin, precision: 1);
    }
}

public sealed class ProbeProgressTests
{
    [Fact]
    public void SinSondasTerminadas_EstaEnCero() =>
        Assert.Equal(0, new ProbeProgress(0, 15, "Identificando el equipo").Percent);

    [Fact]
    public void TodasTerminadas_LlegaACien() =>
        Assert.Equal(100, new ProbeProgress(15, 15, "Listo").Percent);

    [Fact]
    public void AvanceParcial_EsProporcional() =>
        Assert.Equal(40, new ProbeProgress(6, 15, "Memoria").Percent, precision: 1);

    [Fact]
    public void MasTerminadasQueTotales_SeAcotaEnCien()
    {
        // Las sondas corren en paralelo y el contador es compartido: acotar evita que un incremento
        // de más muestre 107 %.
        Assert.Equal(100, new ProbeProgress(20, 15, "x").Percent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void SinTotal_DevuelveCeroSinDividirPorCero(int total) =>
        Assert.Equal(0, new ProbeProgress(5, total, "x").Percent);
}
