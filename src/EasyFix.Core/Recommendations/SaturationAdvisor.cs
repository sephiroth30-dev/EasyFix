using System.Globalization;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;

namespace EasyFix.Core.Recommendations;

/// <summary>
/// Anticipa saturación próxima a partir de lo medido HOY. Lógica pura.
/// </summary>
/// <remarks>
/// <para>Una corrida única no tiene historial, así que acá no hay «tendencias»: hay umbrales de
/// alerta temprana (medición real) y una única proyección — el llenado del disco — cuyo supuesto va
/// escrito dentro del texto. La regla del proyecto manda: si algo es una estimación, el reporte lo
/// dice; lo que no se midió no genera nada.</para>
///
/// <para>Separado de <see cref="HardwareAdvisor"/> porque este habla de HECHOS actuales («la memoria
/// está al 92 %») y este módulo de riesgo próximo («está al 78 %, cerca del límite»). Mezclarlos
/// diluiría la evidencia dura con avisos preventivos.</para>
/// </remarks>
public sealed class SaturationAdvisor
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>GB por mes que consumen las actualizaciones de Windows en un equipo típico.</summary>
    /// <remarks>
    /// Supuesto conservador y documentado (updates mensuales acumulativos + .NET + Defender). Es la
    /// única constante «no medida» del módulo, y por eso el texto del hallazgo la declara.
    /// </remarks>
    private const double AssumedMonthlyUpdateGb = 2.0;

    /// <summary>Desde qué porcentaje de commit se considera «cerca del límite».</summary>
    private const double MemoryWarnPercent = 70;

    private readonly ThresholdOptions _thresholds;

    public SaturationAdvisor(ThresholdOptions thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = thresholds;
    }

    public IReadOnlyList<Finding> Evaluate(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<Finding>();

        AddMemoryNearLimit(snapshot, findings);
        AddDiskFillProjection(snapshot, findings);

        return findings;
    }

    /// <summary>
    /// Memoria entre el umbral de alerta y el de presión: todavía no hay congelamientos, pero la
    /// próxima aplicación pesada los trae. Es el aviso que evita el «ayer andaba bien».
    /// </summary>
    private void AddMemoryNearLimit(SystemSnapshot s, List<Finding> findings)
    {
        if (s.CommitUsedPercent is not double commit ||
            commit < MemoryWarnPercent ||
            commit > _thresholds.CommitPressurePercent)
        {
            return; // sin medición, holgado, o ya en presión (eso lo reporta HardwareAdvisor como hecho)
        }

        var metrics = new List<Metric>
        {
            new("Memoria comprometida", commit.ToString("0.#", Es), "%"),
        };

        string ramText = string.Empty;
        if (s.TotalRamGb is double ram)
        {
            metrics.Add(new Metric("RAM instalada", ram.ToString("0.#", Es), "GB"));
            ramText = $" con {ram.ToString("0.#", Es)} GB de RAM";
        }

        findings.Add(new Finding(
            "saturation.memory",
            Severity.Info,
            "La memoria está cerca del límite",
            $"La memoria comprometida está al {commit.ToString("0.#", Es)} %{ramText}. Todavía no " +
            $"hay congelamientos, pero por encima del {_thresholds.CommitPressurePercent} % el equipo " +
            "empieza a usar el disco como memoria. Si el cliente nota que «a veces se traba», es " +
            "esto: conviene cerrar lo que no se usa del inicio y, si se repite, ampliar la RAM.",
            metrics));
    }

    /// <summary>
    /// Proyección del llenado de C:. La única estimación de la app, con el supuesto en el texto.
    /// </summary>
    private void AddDiskFillProjection(SystemSnapshot s, List<Finding> findings)
    {
        if (s.SystemDriveFreePercent is not double freePercent ||
            s.SystemDriveTotalGb is not double totalGb ||
            totalGb <= 0)
        {
            return;
        }

        double freeGb = totalGb * freePercent / 100.0;

        // Con el disco todavía holgado no se proyecta nada: proyectar 60 meses es ruido.
        // Se avisa cuando quedan menos de 40 GB o menos del 25 %.
        if (freeGb >= 40 && freePercent >= 25)
        {
            return;
        }

        // Por debajo del umbral duro ya es un problema HOY y lo reporta HardwareAdvisor; esta
        // proyección cubre la franja «todavía no duele, pero va a doler».
        if (freePercent < _thresholds.LowDiskFreePercent)
        {
            return;
        }

        int months = Math.Max(1, (int)Math.Floor(
            (freeGb - totalGb * _thresholds.LowDiskFreePercent / 100.0) / AssumedMonthlyUpdateGb));

        findings.Add(new Finding(
            "saturation.disk",
            Severity.Info,
            "El disco se va a quedar sin espacio",
            $"Quedan {freeGb.ToString("0", Es)} GB libres ({freePercent.ToString("0.#", Es)} %). " +
            $"Es una estimación, no una medición: contando solo las actualizaciones de Windows " +
            $"(~{AssumedMonthlyUpdateGb.ToString("0", Es)} GB por mes, sin contar lo que instale el " +
            $"cliente), en unos {months} mes(es) cae debajo del {_thresholds.LowDiskFreePercent} % " +
            "y el equipo se vuelve lento. La limpieza de hoy ayuda; si el cliente guarda fotos o " +
            "video, conviene planear un disco más grande ahora y no cuando ya no arranque.",
            new[]
            {
                new Metric("Espacio libre", freeGb.ToString("0", Es), "GB"),
                new Metric("Estimación hasta el límite", months.ToString(Es), "meses"),
            }));
    }
}
