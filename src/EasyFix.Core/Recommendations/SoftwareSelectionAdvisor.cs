using System.Globalization;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;

namespace EasyFix.Core.Recommendations;

/// <summary>
/// Recomienda qué software conviene (y cuál evitar) según lo medido del equipo. Lógica pura.
/// </summary>
/// <remarks>
/// <para>Es la respuesta a «¿qué le instalo a esta máquina?»: no todo equipo aguanta lo mismo, y el
/// error típico es instalar la suite pesada de siempre en un equipo de 4 GB con disco mecánico.
/// Cada recomendación sale de un dato medido del snapshot; un campo sin medir no recomienda nada.</para>
///
/// <para>No toca el catálogo de winget: dice qué criterio usar al marcar casillas. La selección
/// final sigue siendo del técnico.</para>
/// </remarks>
public sealed class SoftwareSelectionAdvisor
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly ThresholdOptions _thresholds;

    public SoftwareSelectionAdvisor(ThresholdOptions thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = thresholds;
    }

    public IReadOnlyList<Recommendation> Advise(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new List<Recommendation>();

        AddLowRamProfile(snapshot, result);
        AddHddProfile(snapshot, result);
        AddLaptopProfile(snapshot, result);

        return result;
    }

    /// <summary>Equipo con poca RAM: el software liviano es la diferencia entre usable y no.</summary>
    private void AddLowRamProfile(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.TotalRamGb is not double ram || ram >= _thresholds.MinRamGb)
        {
            return;
        }

        result.Add(new Recommendation(
            "software.low-ram",
            RecommendationPriority.Recommended,
            "Software liviano para este equipo",
            $"Con {ram.ToString("0.#", Es)} GB de RAM cada programa pesa. Para que el equipo se " +
            "mantenga usable: como antivirus alcanza Microsoft Defender (ya viene con Windows; una " +
            "suite de terceros se come varios cientos de MB); para PDF, el visor liviano antes que " +
            "la suite completa; y conviene limitar las pestañas del navegador antes que cambiar de " +
            "navegador — todos consumen parecido con las mismas pestañas.",
            new Metric("RAM instalada", ram.ToString("0.#", Es), "GB")));
    }

    /// <summary>Disco mecánico: evitar todo lo que escanea o arranca solo.</summary>
    private static void AddHddProfile(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.IsSsd != false)
        {
            return; // SSD medido o tipo sin identificar: nada que recomendar por disco
        }

        result.Add(new Recommendation(
            "software.hdd",
            RecommendationPriority.Recommended,
            "En disco mecánico, menos es más",
            "El disco mecánico hace que cada programa residente duela: evitá instalar software que " +
            "arranque con Windows o que escanee en segundo plano (suites de antivirus de terceros, " +
            "«optimizadores», asistentes de actualización del fabricante). Defender + los programas " +
            "que el cliente realmente usa es la combinación que mejor anda hasta que se cambie el " +
            "disco por un SSD.",
            new Metric("Disco", "Mecánico (medido)")));
    }

    /// <summary>Batería presente: condiciona qué conviene dejar corriendo.</summary>
    /// <remarks>
    /// «Batería presente», no «es una laptop»: un desktop con UPS también expone batería por WMI.
    /// El consejo vale igual en ambos casos (lo residente gasta energía de respaldo), pero el texto
    /// no afirma el tipo de equipo porque nadie lo midió.
    /// </remarks>
    private static void AddLaptopProfile(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.HasBattery != true)
        {
            return;
        }

        result.Add(new Recommendation(
            "software.laptop",
            RecommendationPriority.Optional,
            "Hay batería: cuidado con lo residente",
            "El equipo reporta una batería (laptop, o un UPS conectado). Cada programa que corre en " +
            "segundo plano gasta esa energía además de memoria: al elegir software, preferí " +
            "versiones que no instalen «ayudantes» de arranque (los lanzadores de juegos y las " +
            "suites de fabricante son los peores). Si es una laptop, el plan de energía queda en " +
            "«Equilibrado»: alto rendimiento recorta la batería sin ganancia perceptible.",
            new Metric("Batería", "Presente")));
    }
}
