using EasyFix.Core.Processes;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Limpieza de la salida de los procesos para el log.
/// </summary>
/// <remarks>
/// El log del 2026-08-29 tenía cuatro líneas que terminaban literalmente en «stderr: » y ni una
/// palabra de winget en 71 líneas: solo se registraba stderr, y winget escribe en stdout. Al empezar a
/// registrar stdout apareció el problema opuesto — la animación de progreso, que son cientos de
/// cuadros del mismo renglón reescrito con <c>\r</c>.
/// </remarks>
public sealed class ProcessOutputTests
{
    [Fact]
    public void DeUnaLineaReescritaSeGuardaElUltimoCuadro()
    {
        // Así llega la barra de progreso de winget capturada a un string.
        const string Animated = "  -\r  \\\r  |\r  /\r  Descargando 45,2 MB\n";

        Assert.Equal("Descargando 45,2 MB", ProcessOutput.Clean(Animated));
    }

    [Fact]
    public void LosRenglonesQueSonSoloAnimacionSeDescartan()
    {
        const string Output = "-\n\\\n|\n/\nInstalación completada\n";

        Assert.Equal("Instalación completada", ProcessOutput.Clean(Output));
    }

    [Fact]
    public void ElTextoRealNoSeToca()
    {
        // Nada de recortar palabras: el log tiene que quedar completo. Una versión anterior de la
        // lógica de mensajes se comió el punto de «Access is denied.» por ser demasiado agresiva.
        const string Output = "Access is denied.\nEl paquete requiere permisos de administrador.";

        Assert.Equal(Output, ProcessOutput.Clean(Output));
    }

    [Fact]
    public void UnaLineaConAnimacionYTextoConservaElTexto()
    {
        // El caso peligroso: si se descartara la línea entera por contener caracteres de animación,
        // se perdería el mensaje.
        Assert.Equal("Encontrado 7-Zip [7zip.7zip]",
            ProcessOutput.Clean("  ▒▒▒\rEncontrado 7-Zip [7zip.7zip]"));
    }

    [Fact]
    public void LosPuntosSuspensivosDeUnMensajeRealNoLoConviertenEnAnimacion()
    {
        // Los puntos están en la lista de caracteres de animación, así que una línea de solo puntos se
        // descarta — pero una con texto y puntos tiene que sobrevivir entera.
        Assert.Equal("Instalando…", ProcessOutput.Clean("Instalando…"));
        Assert.Equal(string.Empty, ProcessOutput.Clean("... ..."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void SalidaVaciaDevuelveVacioSinReventar(string? output) =>
        Assert.Equal(string.Empty, ProcessOutput.Clean(output));

    [Fact]
    public void CortarAvisaCuantoQuedoAfuera()
    {
        // Sin el número no se sabe si se perdió una palabra o veinte pantallas de salida de DISM.
        string truncated = ProcessOutput.Truncate(new string('x', 100), 10);

        Assert.StartsWith(new string('x', 10), truncated);
        Assert.Contains("+90 caracteres", truncated);
    }

    [Fact]
    public void CortarNoTocaLoQueYaCabe()
    {
        const string Short = "cabe entero";

        Assert.Equal(Short, ProcessOutput.Truncate(Short, 500));
    }

    [Fact]
    public void ForLogLimpiaAntesDeCortar()
    {
        // Es el orden que importa: cortar primero se quedaría con 20 caracteres de animación y
        // dejaría el mensaje afuera, que es exactamente el bug que se está evitando.
        string animation = string.Concat(Enumerable.Repeat("-\r\\\r|\r/\r", 200));
        string output = animation + "El paquete se instaló correctamente";

        string logged = ProcessOutput.ForLog(output, 100);

        Assert.Equal("El paquete se instaló correctamente", logged);
        Assert.DoesNotContain("caracteres", logged);
    }
}
