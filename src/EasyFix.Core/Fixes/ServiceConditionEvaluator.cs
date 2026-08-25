using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;

namespace EasyFix.Core.Fixes;

/// <param name="Offer">Si el servicio se le muestra al técnico como opción.</param>
/// <param name="Reason">Por qué. Cuando no se ofrece, se explica el motivo en vez de esconderlo.</param>
public sealed record ServiceOffer(bool Offer, string Reason);

/// <summary>
/// Decide si un servicio de la lista de <c>appsettings.json</c> se ofrece en este equipo.
/// </summary>
/// <remarks>
/// <para>Existe por un caso concreto: <b><c>SysMain</c> (Superfetch) AYUDA en un disco mecánico.</b>
/// Las listas de "servicios para desactivar" que circulan por foros lo incluyen sin condición, y
/// aplicarlo en un equipo con HDD lo deja más lento. Lo mismo con <c>Spooler</c>: desactivarlo en un
/// equipo con impresora rompe la impresión.</para>
///
/// <para><b>Falla cerrado.</b> Una condición que no se reconoce hace que el servicio NO se ofrezca.
/// Un typo en la configuración tiene que resultar en "no hago nada", nunca en "lo desactivo igual".
/// Lo mismo cuando falta el dato para evaluar la condición.</para>
/// </remarks>
public sealed class ServiceConditionEvaluator
{
    public const string NoPrintersInstalled = "NoPrintersInstalled";
    public const string NoBluetoothAdapter = "NoBluetoothAdapter";
    public const string SsdAndRamAtLeast8Gb = "SsdAndRamAtLeast8Gb";

    public ServiceOffer Evaluate(ServiceCandidate candidate, SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Sin condición, se ofrece siempre. Igual sigue siendo Capa 3: checkbox, nunca automático.
        if (string.IsNullOrWhiteSpace(candidate.OnlyIf))
        {
            return new ServiceOffer(true, "Sin condiciones.");
        }

        return candidate.OnlyIf switch
        {
            NoPrintersInstalled => snapshot.HasPrinters
                ? new ServiceOffer(false, "El equipo tiene impresoras instaladas: desactivarlo rompería la impresión.")
                : new ServiceOffer(true, "No se detectaron impresoras."),

            NoBluetoothAdapter => snapshot.HasBluetoothAdapter
                ? new ServiceOffer(false, "El equipo tiene Bluetooth: desactivarlo dejaría de funcionar.")
                : new ServiceOffer(true, "No se detectó adaptador Bluetooth."),

            SsdAndRamAtLeast8Gb => EvaluateSysMainCondition(snapshot),

            // Condición desconocida: probablemente un typo en appsettings.json. No se ofrece.
            _ => new ServiceOffer(
                false,
                $"La condición '{candidate.OnlyIf}' no está implementada, así que no se ofrece " +
                "este servicio. Revisá appsettings.json."),
        };
    }

    /// <summary>
    /// Filtra la lista completa, devolviendo solo los que corresponden a este equipo junto con el
    /// motivo de cada decisión.
    /// </summary>
    public IReadOnlyList<(ServiceCandidate Candidate, ServiceOffer Offer)> EvaluateAll(
        IEnumerable<ServiceCandidate> candidates,
        SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Select(c => (c, Evaluate(c, snapshot))).ToList();
    }

    private static ServiceOffer EvaluateSysMainCondition(SystemSnapshot snapshot)
    {
        if (snapshot.PrimaryDiskMedia == DiskMedia.Unknown)
        {
            // No se pudo determinar el tipo de disco. Si fuera mecánico, desactivar SysMain
            // empeoraría el equipo: sin el dato, no se toca.
            return new ServiceOffer(
                false,
                "No se pudo determinar si el disco es SSD o mecánico. En disco mecánico este " +
                "servicio ayuda, así que no se ofrece sin ese dato.");
        }

        if (!snapshot.IsSsd)
        {
            return new ServiceOffer(
                false,
                "El equipo tiene disco mecánico y en ese caso este servicio MEJORA el rendimiento: " +
                "precarga los programas más usados. Desactivarlo sería contraproducente.");
        }

        if (snapshot.TotalRamGb is not double ramGb)
        {
            return new ServiceOffer(false, "No se pudo determinar la RAM instalada.");
        }

        // 7.5 y no 8: 8 GB nominales se reportan como 7.9x GB útiles.
        const double MinimumRamGb = 7.5;

        return ramGb < MinimumRamGb
            ? new ServiceOffer(
                false,
                $"El equipo tiene {ramGb:0.#} GB de RAM. Con menos de 8 GB la precarga ayuda más " +
                "de lo que molesta.")
            : new ServiceOffer(true, $"Disco SSD y {ramGb:0.#} GB de RAM: la precarga aporta poco.");
    }
}
