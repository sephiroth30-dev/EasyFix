using EasyFix.Core.Rollback;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests de la decisión «¿el punto de restauración quedó creado?».
/// </summary>
/// <remarks>
/// Es la lógica que en la primera prueba real dio falso negativo en las cinco corridas, con la
/// consecuencia peor posible: el técnico terminó trabajando sin red de seguridad creyendo que no la
/// tenía, cuando sí existía. Los casos de abajo son exactamente los del log.
/// </remarks>
public sealed class RestorePointPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 13, 6, 37, TimeSpan.Zero);

    private static RestorePointInfo Point(long sequence, DateTimeOffset? created) =>
        new(sequence, created, "EasyFix");

    // ---- La señal principal: la fecha ------------------------------------------------------

    [Fact]
    public void PuntoReciente_EsCreado_AunqueLaSecuenciaNoHayaSubido()
    {
        // ESTE es el caso del log: secuencia 210 → 210, y el punto igual existía. La API es
        // asíncrona, así que la secuencia todavía no reflejaba el punto nuevo.
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(210, Now.AddSeconds(-3)), sequenceBefore: 210, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    [Fact]
    public void PuntoReciente_EsCreado_AunqueNoSeHayaPodidoLeerElEstadoPrevio()
    {
        // El otro defecto del log: la lectura previa falló y se interpretó como "no hay ninguno".
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(213, Now.AddSeconds(-1)), sequenceBefore: null, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    [Fact]
    public void PuntoViejo_NoAlcanzaParaDarloPorCreado()
    {
        // Un punto de ayer no es el nuestro. Sin esto la verificación sería un sello de goma.
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(210, Now.AddHours(-20)), sequenceBefore: 210, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.KeepWaiting, verdict);
    }

    [Theory]
    [InlineData(-9)]    // hace 9 minutos: dentro de la ventana
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]     // 9 minutos en el futuro: reloj adelantado, se acepta igual
    public void LaVentanaDeFrescuraToleraRelojesDesfasados(int minutesOffset)
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(1, Now.AddMinutes(minutesOffset)), sequenceBefore: 1, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public void FueraDeLaVentana_NoCuentaPorFecha(int minutesOffset)
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(1, Now.AddMinutes(minutesOffset)), sequenceBefore: 1, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.KeepWaiting, verdict);
    }

    // ---- La señal de respaldo: la secuencia -------------------------------------------------

    [Fact]
    public void SecuenciaQueSubio_EsCreado_AunqueLaFechaSeaIlegible()
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(214, created: null), sequenceBefore: 213, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    [Fact]
    public void SecuenciaQueSubio_EsCreado_AunqueLaFechaSeaVieja()
    {
        // Reloj del equipo muy desfasado: la secuencia salva la verificación.
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(214, Now.AddYears(-2)), sequenceBefore: 213, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    [Fact]
    public void FechaIlegibleYSinLecturaPrevia_NoSePuedeConcluirNada()
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(214, created: null), sequenceBefore: null, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.KeepWaiting, verdict);
    }

    // ---- No poder leer no es un hecho ------------------------------------------------------

    [Fact]
    public void LecturaFallida_SigueEsperando()
    {
        // El log muestra una consulta que devolvió nada y veinte segundos después devolvía 213.
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            newest: null, sequenceBefore: 213, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.KeepWaiting, verdict);
    }

    [Fact]
    public void LecturaFallidaSinMasIntentos_NoSePuedeVerificar()
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            newest: null, sequenceBefore: 213, Now, attemptsRemain: false);

        Assert.Equal(RestorePointCheck.CannotVerify, verdict);
    }

    [Fact]
    public void SinIntentosRestantes_YSinSeñal_NoSePuedeVerificar()
    {
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(210, Now.AddHours(-20)), sequenceBefore: 210, Now, attemptsRemain: false);

        Assert.Equal(RestorePointCheck.CannotVerify, verdict);
    }

    // ---- El equipo sin ningún punto previo -------------------------------------------------

    [Fact]
    public void PrimerPuntoDelEquipo_SeReconoce()
    {
        // Equipo sin puntos: la lectura previa devuelve 0 legítimamente, no null.
        RestorePointCheck verdict = RestorePointPolicy.Evaluate(
            Point(1, Now.AddSeconds(-2)), sequenceBefore: 0, Now, attemptsRemain: true);

        Assert.Equal(RestorePointCheck.Created, verdict);
    }

    // ---- La fecha de WMI ------------------------------------------------------------------

    [Fact]
    public void ParseaLaFechaDeWmiConDesplazamiento()
    {
        // Formato real: yyyyMMddHHmmss.ffffff±minutos. -300 son 5 horas menos (Colombia).
        DateTimeOffset? parsed = RestorePointPolicy.ParseWmiDate("20260825130637.000000-300");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 13, 6, 37, TimeSpan.FromMinutes(-300)), parsed);
    }

    [Fact]
    public void ParseaLaFechaDeWmiConDesplazamientoPositivo()
    {
        DateTimeOffset? parsed = RestorePointPolicy.ParseWmiDate("20260825130637.000000+120");

        Assert.Equal(new DateTimeOffset(2026, 8, 25, 13, 6, 37, TimeSpan.FromMinutes(120)), parsed);
    }

    [Fact]
    public void FechaDeWmiSinDesplazamiento_SeInterpretaComoLocal()
    {
        DateTimeOffset? parsed = RestorePointPolicy.ParseWmiDate("20260825130637");

        Assert.NotNull(parsed);
        Assert.Equal(13, parsed!.Value.Hour);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("corto")]
    [InlineData("basuraaaaaaaaaaaaaa")]
    [InlineData("20261325130637.000000-300")]   // mes 13: fecha imposible
    [InlineData("20260832130637.000000-300")]   // día 32
    public void FechaDeWmiInvalida_DevuelveNullSinReventar(string? value) =>
        Assert.Null(RestorePointPolicy.ParseWmiDate(value));
}
