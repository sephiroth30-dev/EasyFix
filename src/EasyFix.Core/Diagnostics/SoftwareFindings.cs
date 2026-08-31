using System.Globalization;
using EasyFix.Core.Configuration;
using EasyFix.Core.Network;

namespace EasyFix.Core.Diagnostics;

/// <summary>
/// Reglas sobre el snapshot que producen hallazgos de software: cosas que se arreglan sin comprar
/// nada.
/// </summary>
/// <remarks>
/// Lógica pura, sin dependencias de Windows: <see cref="SystemProbe"/> ya hizo todas las lecturas.
/// Eso es lo que permite testear estas reglas desde cualquier sistema, y que agregar una regla nueva
/// no requiera una VM.
/// </remarks>
public static class SoftwareFindings
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static IReadOnlyList<Finding> Evaluate(SystemSnapshot snapshot, ThresholdOptions thresholds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);

        var findings = new List<Finding>();

        AddStartupEntries(snapshot, findings);
        AddBitLockerNotice(snapshot, findings);
        AddDomainNotice(snapshot, findings);
        AddNetworkNotice(snapshot, findings);

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddStartupEntries(SystemSnapshot s, List<Finding> findings)
    {
        if (s.EnabledStartupEntryCount is not int count || count == 0)
        {
            return;
        }

        var metrics = new List<Metric>
        {
            new("Programas en el inicio", count.ToString(Es)),
        };

        // La degradación medida es el argumento fuerte: convierte "tenés muchos programas" en
        // "te cuestan 31 segundos".
        string detail;
        Severity severity;

        if (s.StartupDegradationMs is int ms && ms > 0)
        {
            double seconds = ms / 1000.0;
            metrics.Add(new Metric("Retraso medido en el arranque", seconds.ToString("0.#", Es), "s"));

            severity = seconds >= 10 ? Severity.Warning : Severity.Info;
            detail =
                $"Hay {count} programas configurados para arrancar con Windows y, según el registro " +
                $"de eventos, le suman {seconds.ToString("0.#", Es)} s al arranque. Los que se puedan " +
                "desactivar sin riesgo se desactivan solos; el resto queda para que decidas.";
        }
        else
        {
            // Sin la medición, el conteo solo alcanza para un aviso.
            severity = count >= 10 ? Severity.Warning : Severity.Info;
            detail =
                $"Hay {count} programas configurados para arrancar con Windows. No se pudo medir " +
                "cuánto retrasan el arranque en este equipo (el registro de rendimiento de Windows " +
                "todavía no tiene datos), así que solo se informa la cantidad.";
        }

        findings.Add(new Finding(
            "startup.entries",
            severity,
            $"{count} programas arrancan con Windows",
            detail,
            metrics,
            new[] { "startup.disable" }));
    }

    private static void AddBitLockerNotice(SystemSnapshot s, List<Finding> findings)
    {
        if (!s.BitLockerActive)
        {
            return;
        }

        findings.Add(new Finding(
            "bitlocker.active",
            Severity.Warning,
            "Este equipo tiene BitLocker activo",
            "Las reparaciones que tocan el disco o el arranque pueden hacer que Windows pida la " +
            "clave de recuperación de 48 dígitos al reiniciar. Sin esa clave el cliente queda fuera " +
            "de su propio equipo. Esas reparaciones quedan bloqueadas hasta que confirmes que la " +
            "clave está a mano; el resto de las mejoras corre igual.",
            new[] { new Metric("BitLocker", "Activo en C:") }));
    }

    private static void AddDomainNotice(SystemSnapshot s, List<Finding> findings)
    {
        if (!s.IsDomainJoined)
        {
            return;
        }

        findings.Add(new Finding(
            "domain.joined",
            Severity.Info,
            "El equipo está unido a un dominio",
            "Las políticas de la empresa revierten muchos cambios en la próxima actualización de " +
            "directivas: el arreglo parece funcionar y se deshace solo. Además, desactivar agentes " +
            "de gestión o la VPN corporativa rompe el equipo para el área de sistemas. Se activa el " +
            "modo restringido: solo limpieza de temporales y cachés.",
            new[] { new Metric("Modo", "Restringido") }));
    }

    /// <summary>
    /// Avisa si el equipo no tiene internet, con el motivo concreto.
    /// </summary>
    /// <remarks>
    /// <para>Sale en el reporte, o sea <b>antes</b> de que el técnico apriete «Instalar programas». En
    /// el equipo del 2026-08-29 se enteró al revés: probó a instalar cuatro programas, fallaron los
    /// cuatro, y el log le echó la culpa al catálogo de winget.</para>
    ///
    /// <para>Un estado sin medir (<c>null</c>) no genera hallazgo: no se afirma que haya internet ni
    /// que falte.</para>
    /// </remarks>
    private static void AddNetworkNotice(SystemSnapshot s, List<Finding> findings)
    {
        if (s.Network is not ConnectivityStatus status || status == ConnectivityStatus.Online)
        {
            return;
        }

        (string title, string detail) = status switch
        {
            ConnectivityStatus.NoAdapter => (
                "El equipo no está conectado a ninguna red",
                "No hay cable de red conectado ni wifi asociado."),

            ConnectivityStatus.DnsFailed => (
                "El equipo no tiene internet",
                "Está conectado a la red local pero no puede resolver nombres: o no hay salida a " +
                "internet, o el DNS está mal configurado."),

            ConnectivityStatus.CaptivePortal => (
                "La red exige iniciar sesión en el navegador",
                "Es una red de hotel, aeropuerto o cafetería: hay que abrir el navegador y aceptar " +
                "su página antes de tener internet."),

            ConnectivityStatus.Unknown => (
                "No se pudo comprobar si hay internet",
                "La comprobación no se pudo completar. Se asume que no hay conexión, que es lo " +
                "seguro: no se va a intentar descargar nada."),

            _ => (
                "El equipo no llega a internet",
                "Hay red pero la conexión no sale. Suele ser un firewall, un antivirus o un proxy."),
        };

        findings.Add(new Finding(
            "network.offline",
            Severity.Warning,
            title,
            detail + " «Instalar programas» no va a funcionar: EasyFix descarga todo en el momento " +
                     "para que entre siempre la última versión, así que nada viene dentro del programa. " +
                     "El resto —analizar, mejorar el rendimiento y reparar errores— sí funciona sin internet.",
            new[] { new Metric("Conexión", DescribeStatus(status)) }));
    }

    private static string DescribeStatus(ConnectivityStatus status) => status switch
    {
        ConnectivityStatus.NoAdapter => "Sin red",
        ConnectivityStatus.DnsFailed => "Sin DNS",
        ConnectivityStatus.Unreachable => "Sin salida",
        ConnectivityStatus.CaptivePortal => "Portal cautivo",
        ConnectivityStatus.Unknown => "No comprobada",
        _ => status.ToString(),
    };
}
