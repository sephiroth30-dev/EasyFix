using System.Globalization;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;

namespace EasyFix.Core.Recommendations;

/// <summary>Urgencia de una recomendación de hardware.</summary>
public enum RecommendationPriority
{
    /// <summary>Mejora opcional.</summary>
    Optional = 0,

    /// <summary>Vale la pena, mejora notoria.</summary>
    Recommended = 1,

    /// <summary>La mejora #1 para este equipo.</summary>
    TopImpact = 2,

    /// <summary>Hay que actuar antes de optimizar nada.</summary>
    Urgent = 3,
}

/// <param name="Id">Identificador estable para el reporte.</param>
/// <param name="Priority">Urgencia.</param>
/// <param name="Title">Título corto.</param>
/// <param name="Detail">Explicación en español para el cliente, con el número medido dentro.</param>
/// <param name="Evidence">La medición que la sustenta. Sin esto sería una corazonada.</param>
/// <param name="BlocksFixes">
/// <c>true</c> cuando aplicar fixes sería contraproducente o peligroso. Hoy solo lo activa un disco
/// con SMART fallando.
/// </param>
public sealed record Recommendation(
    string Id,
    RecommendationPriority Priority,
    string Title,
    string Detail,
    Metric? Evidence = null,
    bool BlocksFixes = false);

/// <summary>
/// Traduce lo medido en recomendaciones de hardware.
/// </summary>
/// <remarks>
/// <para>Es la parte honesta de la app. El software no hace que un equipo vaya 200% más rápido; un
/// SSD sí. Cada recomendación sale de un número medido y lo lleva adentro del texto, para que el
/// cliente vea la evidencia y no una opinión.</para>
///
/// <para><b>Cuando un dato no se midió, no se recomienda nada sobre él.</b> Todos los campos de
/// <see cref="SystemSnapshot"/> son opcionales y un <c>null</c> significa "no sé", no "cero". Inventar
/// una recomendación sobre un dato ausente es peor que quedarse callado: el cliente gasta plata.</para>
/// </remarks>
public sealed class HardwareAdvisor
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly ThresholdOptions _thresholds;

    public HardwareAdvisor(ThresholdOptions thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = thresholds;
    }

    /// <summary>
    /// Devuelve las recomendaciones ordenadas de más urgente a menos. Lista vacía cuando el hardware
    /// del equipo está bien: en ese caso la respuesta correcta es no vender nada.
    /// </summary>
    public IReadOnlyList<Recommendation> Advise(SystemSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new List<Recommendation>();

        AddFailingDisk(snapshot, result);
        AddMechanicalDisk(snapshot, result);
        AddInsufficientRam(snapshot, result);
        AddLowDiskSpace(snapshot, result);
        AddSlowDisk(snapshot, result);
        AddBatteryWear(snapshot, result);
        AddCpuThrottling(snapshot, result);
        AddConflictingAntivirus(snapshot, result);

        return result
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <c>true</c> si aplicar fixes sería contraproducente. Se consulta antes de cualquier cambio.
    /// </summary>
    public bool ShouldBlockFixes(SystemSnapshot snapshot) =>
        Advise(snapshot).Any(r => r.BlocksFixes);

    // ---- Reglas -----------------------------------------------------------------------------

    private static void AddFailingDisk(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.PrimaryDiskHealth != DiskHealth.Failing)
        {
            return;
        }

        result.Add(new Recommendation(
            "disk.failing",
            RecommendationPriority.Urgent,
            "El disco está fallando",
            "SMART predice una falla del disco. Respaldá los datos hoy y reemplazá el disco. " +
            "No tiene sentido optimizar: cada escritura acerca la falla y puede perderse todo.",
            new Metric("Estado SMART", "Predice falla"),
            BlocksFixes: true));
    }

    private static void AddMechanicalDisk(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.PrimaryDiskMedia != DiskMedia.Hdd)
        {
            return;
        }

        // Si se midió el arranque, se da el número real y una estimación conservadora. Si no, se
        // describe la mejora sin inventar segundos.
        string detail;
        Metric? evidence = null;

        if (s.MainPathBootTimeMs is int bootMs && bootMs > 0)
        {
            double currentSeconds = bootMs / 1000.0;

            // Un SSD SATA no baja el arranque por debajo de ~15 s en un equipo con este perfil. Se
            // usa un piso en vez de una regla de tres, que daría números absurdos.
            double estimated = Math.Max(15, currentSeconds / 4);

            detail =
                $"El equipo tiene disco mecánico. Hoy tarda {Format(currentSeconds)} s en arrancar; " +
                $"con un SSD SATA quedaría cerca de {Format(estimated)} s. " +
                "Es la mejora #1 y ninguna optimización de software se le acerca.";
            evidence = new Metric("Tiempo de arranque", Format(currentSeconds), "s");
        }
        else
        {
            detail =
                "El equipo tiene disco mecánico. Cambiarlo por un SSD es la mejora #1 y ninguna " +
                "optimización de software se le acerca: el arranque y la apertura de programas " +
                "mejoran varias veces.";
        }

        if (s.SystemDriveTotalGb is double totalGb && totalGb > 0)
        {
            detail += $" Tu disco actual es de {Format(totalGb)} GB, así que buscá un SSD de al menos " +
                      $"{RecommendedSsdSizeGb(totalGb)} GB.";
        }

        result.Add(new Recommendation(
            "disk.mechanical",
            RecommendationPriority.TopImpact,
            "Cambiar el disco mecánico por un SSD",
            detail,
            evidence));
    }

    private void AddInsufficientRam(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.TotalRamGb is not double ramGb)
        {
            return; // no se midió: no se recomienda
        }

        bool belowMinimum = ramGb < _thresholds.MinRamGb;
        bool underPressure = s.CommitUsedPercent is double commit && commit > _thresholds.CommitPressurePercent;

        if (!belowMinimum && !underPressure)
        {
            return;
        }

        // Poca RAM sin presión de memoria es sospecha; poca RAM CON presión es un hecho medido.
        RecommendationPriority priority = underPressure
            ? RecommendationPriority.TopImpact
            : RecommendationPriority.Recommended;

        var detail = new System.Text.StringBuilder();
        detail.Append($"El equipo tiene {Format(ramGb)} GB de RAM. ");

        if (underPressure)
        {
            detail.Append(
                $"La memoria comprometida está al {Format(s.CommitUsedPercent!.Value)} %: el equipo " +
                "está usando el disco como memoria, que es la causa de los congelamientos. ");
        }

        detail.Append($"Conviene llevarlo a {TargetRamGb(ramGb)} GB");

        if (s.MemoryType is not null)
        {
            detail.Append($" con módulos {s.MemoryType}");
            if (s.MemorySpeedMhz is int mhz && mhz > 0)
            {
                detail.Append($"-{mhz}");
            }
        }

        detail.Append('.');

        detail.Append(s.FreeMemorySlots switch
        {
            > 0 => $" Tenés {s.FreeMemorySlots} slot(s) libre(s), así que se puede agregar sin " +
                   "descartar los módulos actuales.",
            0 => " No hay slots libres: hay que reemplazar los módulos instalados, no agregar.",
            _ => string.Empty, // null: no se pudo determinar
        });

        result.Add(new Recommendation(
            "memory.insufficient",
            priority,
            "Ampliar la memoria RAM",
            detail.ToString(),
            new Metric("RAM instalada", Format(ramGb), "GB")));
    }

    private void AddLowDiskSpace(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.SystemDriveFreePercent is not double freePercent ||
            freePercent >= _thresholds.LowDiskFreePercent)
        {
            return;
        }

        result.Add(new Recommendation(
            "disk.low-space",
            RecommendationPriority.Recommended,
            "Liberar espacio en el disco del sistema",
            $"Queda {Format(freePercent)} % libre en C:. Por debajo del " +
            $"{_thresholds.LowDiskFreePercent} % Windows no tiene margen para actualizaciones ni " +
            "para el archivo de paginación, y todo se vuelve más lento. La limpieza de temporales " +
            "ayuda; si no alcanza, hace falta un disco más grande.",
            new Metric("Espacio libre en C:", Format(freePercent), "%")));
    }

    private void AddSlowDisk(SystemSnapshot s, List<Recommendation> result)
    {
        // Si es mecánico ya se recomendó el SSD; repetirlo con otra etiqueta es ruido.
        if (s.PrimaryDiskMedia == DiskMedia.Hdd ||
            s.DiskLatencyMs is not double latency ||
            latency <= _thresholds.DiskLatencyWarnMs)
        {
            return;
        }

        result.Add(new Recommendation(
            "disk.slow",
            RecommendationPriority.Recommended,
            "El disco responde lento para ser un SSD",
            $"La latencia media del disco es de {Format(latency)} ms, por encima de los " +
            $"{_thresholds.DiskLatencyWarnMs} ms esperables. En un SSD esto suele indicar que está " +
            "casi lleno, que perdió rendimiento por desgaste, o que el controlador está en modo IDE " +
            "en vez de AHCI.",
            new Metric("Latencia del disco", Format(latency), "ms")));
    }

    private void AddBatteryWear(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.BatteryWearPercent is not double wear || wear <= _thresholds.BatteryWearPercent)
        {
            return;
        }

        result.Add(new Recommendation(
            "battery.worn",
            RecommendationPriority.Optional,
            "La batería está desgastada",
            $"La batería perdió {Format(wear)} % de su capacidad original. Además de durar menos, " +
            "hace que el procesador se limite cuando el equipo anda sin cargador.",
            new Metric("Desgaste de batería", Format(wear), "%")));
    }

    private void AddCpuThrottling(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.CpuPercentOfMaxFrequency is not double percent ||
            percent >= _thresholds.CpuThrottleFrequencyPercent)
        {
            return;
        }

        result.Add(new Recommendation(
            "cpu.throttling",
            RecommendationPriority.Recommended,
            "El procesador está limitándose",
            $"El procesador{(s.CpuName is null ? string.Empty : $" ({s.CpuName})")} está corriendo al " +
            $"{Format(percent)} % de su frecuencia máxima. Lo habitual es que sea calor: ventilador " +
            "sucio o pasta térmica seca. Limpiar y recambiar la pasta suele recuperar el rendimiento " +
            "sin cambiar nada.",
            new Metric("Frecuencia del CPU", Format(percent), "% del máximo")));
    }

    private static void AddConflictingAntivirus(SystemSnapshot s, List<Recommendation> result)
    {
        if (s.ActiveAntivirusCount < 2)
        {
            return;
        }

        result.Add(new Recommendation(
            "antivirus.conflict",
            RecommendationPriority.TopImpact,
            "Hay más de un antivirus activo",
            $"Se detectaron {s.ActiveAntivirusCount} antivirus activos. Dos antivirus escanean los " +
            "mismos archivos y se estorban entre sí: es una de las causas más frecuentes de lentitud " +
            "severa, y encima protegen peor que uno solo. Hay que dejar uno.",
            new Metric("Antivirus activos", s.ActiveAntivirusCount.ToString(Es))));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>Un decimal, con coma: es lo que espera un lector hispanohablante.</summary>
    private static string Format(double value) => value.ToString("0.#", Es);

    /// <summary>Objetivo de RAM: el siguiente escalón razonable, tope 32 GB.</summary>
    private static int TargetRamGb(double currentGb) => currentGb switch
    {
        < 4.5 => 8,
        < 8.5 => 16,
        < 16.5 => 32,
        _ => 32,
    };

    /// <summary>Tamaño de SSD sugerido: el escalón comercial que no obliga a borrar datos.</summary>
    private static int RecommendedSsdSizeGb(double currentTotalGb) => currentTotalGb switch
    {
        <= 240 => 240,
        <= 480 => 480,
        <= 1000 => 1000,
        _ => 2000,
    };
}
