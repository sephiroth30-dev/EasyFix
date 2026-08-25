using System.Globalization;

namespace EasyFix.Core.Diagnostics;

/// <summary>Primer sospechoso de un código de parada. Determina qué hay que revisar.</summary>
public enum CrashSuspect
{
    Unknown,

    /// <summary>Memoria RAM. Se prueba con el diagnóstico de memoria; no se arregla con software.</summary>
    Memory,

    /// <summary>Disco o sistema de archivos.</summary>
    Disk,

    /// <summary>Un driver. Se puede revertir a una versión anterior.</summary>
    Driver,

    /// <summary>Driver de video en particular.</summary>
    GraphicsDriver,

    /// <summary>Archivos de sistema corruptos. Esto sí lo repara DISM/SFC.</summary>
    SystemFiles,

    /// <summary>Hardware con falla reportada por el propio firmware. Ninguna actualización lo explica.</summary>
    Hardware,
}

/// <param name="Code">Código de parada normalizado, en minúscula: <c>0x0000007e</c>.</param>
/// <param name="Name">Nombre oficial: <c>SYSTEM_THREAD_EXCEPTION_NOT_HANDLED</c>.</param>
/// <param name="Suspect">Primer sospechoso.</param>
/// <param name="Meaning">Explicación en español.</param>
public sealed record BugCheck(string Code, string Name, CrashSuspect Suspect, string Meaning);

/// <summary>
/// Traduce un código de parada a su primer sospechoso.
/// </summary>
/// <remarks>
/// El código es lo que decide qué revisar. Sin él, "tiene pantallazos" no orienta a nada y se
/// termina reinstalando Windows en un equipo con la RAM mala, que va a volver a fallar.
/// </remarks>
public static class BugCheckCatalog
{
    private static readonly Dictionary<string, BugCheck> Known = Build();

    /// <summary>Busca el código. Devuelve un <see cref="BugCheck"/> genérico si no está catalogado.</summary>
    public static BugCheck Lookup(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new BugCheck("desconocido", "SIN CÓDIGO", CrashSuspect.Unknown,
                "No se pudo extraer el código de parada del registro de eventos.");
        }

        string key = Normalize(code);

        return Known.TryGetValue(key, out BugCheck? found)
            ? found
            : new BugCheck(key, "NO CATALOGADO", CrashSuspect.Unknown,
                $"El código {key} no está en el catálogo. Hace falta analizar el volcado de memoria " +
                "para identificar el driver responsable.");
    }

    /// <summary><c>0x7E</c>, <c>0x0000007e</c> y <c>7e</c> son el mismo código.</summary>
    public static string Normalize(string code)
    {
        string trimmed = code.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("0x", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
            ? "0x" + value.ToString("x8", CultureInfo.InvariantCulture)
            : "0x" + trimmed.PadLeft(8, '0');
    }

    private static Dictionary<string, BugCheck> Build()
    {
        var entries = new[]
        {
            new BugCheck("0x0000000a", "IRQL_NOT_LESS_OR_EQUAL", CrashSuspect.Driver,
                "Un driver accedió a memoria que no le correspondía."),
            new BugCheck("0x0000001a", "MEMORY_MANAGEMENT", CrashSuspect.Memory,
                "Falla en la administración de memoria. Primero se sospecha la RAM; después, un driver."),
            new BugCheck("0x0000001e", "KMODE_EXCEPTION_NOT_HANDLED", CrashSuspect.Driver,
                "Excepción no manejada en modo kernel. Casi siempre un driver."),
            new BugCheck("0x00000024", "NTFS_FILE_SYSTEM", CrashSuspect.Disk,
                "Falla en el sistema de archivos NTFS. Disco o corrupción del volumen."),
            new BugCheck("0x0000003b", "SYSTEM_SERVICE_EXCEPTION", CrashSuspect.Driver,
                "Muy frecuente después de actualizar drivers, sobre todo de video."),
            new BugCheck("0x00000050", "PAGE_FAULT_IN_NONPAGED_AREA", CrashSuspect.Memory,
                "Se accedió a memoria inválida. La causa #1 es RAM defectuosa; después, un driver."),
            new BugCheck("0x0000007a", "KERNEL_DATA_INPAGE_ERROR", CrashSuspect.Disk,
                "Windows no pudo leer del disco. Disco fallando o cable suelto."),
            new BugCheck("0x0000007e", "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashSuspect.Driver,
                "Un hilo del sistema lanzó una excepción que nadie atendió. Driver."),
            new BugCheck("0x0000007f", "UNEXPECTED_KERNEL_MODE_TRAP", CrashSuspect.Hardware,
                "Trampa inesperada del procesador. RAM o CPU; a veces overclock."),
            new BugCheck("0x0000009f", "DRIVER_POWER_STATE_FAILURE", CrashSuspect.Driver,
                "Un driver no respondió al suspender o reanudar el equipo."),
            new BugCheck("0x000000be", "ATTEMPTED_WRITE_TO_READONLY_MEMORY", CrashSuspect.Driver,
                "Un driver intentó escribir en memoria de solo lectura."),
            new BugCheck("0x000000c2", "BAD_POOL_CALLER", CrashSuspect.Driver,
                "Un driver usó mal la memoria del kernel."),
            new BugCheck("0x000000c4", "DRIVER_VERIFIER_DETECTED_VIOLATION", CrashSuspect.Driver,
                "El verificador de controladores detectó un driver haciendo algo ilegal."),
            new BugCheck("0x000000d1", "DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashSuspect.Driver,
                "Driver, típicamente de red."),
            new BugCheck("0x000000ef", "CRITICAL_PROCESS_DIED", CrashSuspect.SystemFiles,
                "Murió un proceso crítico de Windows. Corrupción del sistema, o malware."),
            new BugCheck("0x000000f4", "CRITICAL_OBJECT_TERMINATION", CrashSuspect.Disk,
                "Terminó un objeto crítico. Casi siempre el disco."),
            new BugCheck("0x000000f7", "DRIVER_OVERRAN_STACK_BUFFER", CrashSuspect.Driver,
                "Un driver desbordó su pila."),
            new BugCheck("0x00000109", "CRITICAL_STRUCTURE_CORRUPTION", CrashSuspect.Memory,
                "Se corrompieron estructuras del kernel. RAM, o un driver pisando memoria ajena."),
            new BugCheck("0x00000116", "VIDEO_TDR_ERROR", CrashSuspect.GraphicsDriver,
                "La placa de video no respondió y no se pudo reiniciar. Driver de video, o la placa."),
            new BugCheck("0x00000124", "WHEA_UNCORRECTABLE_ERROR", CrashSuspect.Hardware,
                "El propio hardware reportó un error que no se puede corregir. CPU, RAM o placa."),
            new BugCheck("0x00000133", "DPC_WATCHDOG_VIOLATION", CrashSuspect.Driver,
                "Un driver monopolizó el procesador. Frecuente con controladores SATA o de SSD viejos."),
            new BugCheck("0x00000139", "KERNEL_SECURITY_CHECK_FAILURE", CrashSuspect.Memory,
                "Una comprobación de integridad del kernel falló. RAM o driver."),
            new BugCheck("0x0000013a", "KERNEL_MODE_HEAP_CORRUPTION", CrashSuspect.Driver,
                "Se corrompió el heap del kernel. Driver."),
        };

        return entries.ToDictionary(e => e.Code, StringComparer.Ordinal);
    }
}

/// <summary>Un pantallazo azul registrado.</summary>
/// <param name="When">Cuándo.</param>
/// <param name="RawCode">El código tal como apareció en el evento.</param>
public sealed record CrashEvent(DateTimeOffset When, string? RawCode)
{
    public BugCheck BugCheck => BugCheckCatalog.Lookup(RawCode);
}

/// <summary>Una actualización instalada, para cruzar fechas.</summary>
public sealed record InstalledUpdate(string Id, DateTimeOffset InstalledOn, string? Description = null);

/// <summary>
/// Datos crudos de inestabilidad que lee <see cref="SystemProbe"/>.
/// </summary>
/// <param name="Crashes">Pantallazos del registro de eventos.</param>
/// <param name="MinidumpCount">Archivos de volcado presentes.</param>
/// <param name="WheaErrorCount">Eventos WHEA: el hardware reportando fallas.</param>
/// <param name="UnexpectedShutdownCount">Kernel-Power 41.</param>
/// <param name="DiskErrorCount">Eventos de <c>disk</c> / <c>Ntfs</c> / <c>volmgr</c>.</param>
/// <param name="RecentUpdates">Actualizaciones instaladas en la ventana analizada.</param>
/// <param name="ProblemDeviceNames">Dispositivos con <c>ConfigManagerErrorCode</c> distinto de 0.</param>
/// <param name="WindowDays">Cuántos días abarca la ventana.</param>
public sealed record CrashData(
    IReadOnlyList<CrashEvent> Crashes,
    int MinidumpCount,
    int WheaErrorCount,
    int UnexpectedShutdownCount,
    int DiskErrorCount,
    IReadOnlyList<InstalledUpdate> RecentUpdates,
    IReadOnlyList<string> ProblemDeviceNames,
    int WindowDays)
{
    public static CrashData Empty { get; } = new(
        Array.Empty<CrashEvent>(), 0, 0, 0, 0,
        Array.Empty<InstalledUpdate>(), Array.Empty<string>(), 30);

    public bool HasCrashes => Crashes.Count > 0 || MinidumpCount > 0;
}

/// <param name="Data">Los datos crudos.</param>
/// <param name="DominantSuspect">El sospechoso más frecuente entre los pantallazos.</param>
/// <param name="SuspectCounts">Cuántos pantallazos por sospechoso.</param>
/// <param name="CorrelatedUpdates">
/// Actualizaciones instaladas en los 7 días previos a algún pantallazo. <b>Coincidir no es causar</b>:
/// si el sospechoso es RAM o disco, la actualización solo destapó un problema de hardware.
/// </param>
/// <param name="HardwareConfirmed">
/// Hay evidencia de falla de hardware: eventos WHEA, o un código de parada de categoría hardware.
/// Cuando es <c>true</c>, ninguna reparación de software resuelve el problema.
/// </param>
/// <param name="FirstCrash">El pantallazo más antiguo de la ventana. Es el dato más informativo.</param>
public sealed record CrashAnalysis(
    CrashData Data,
    CrashSuspect DominantSuspect,
    IReadOnlyDictionary<CrashSuspect, int> SuspectCounts,
    IReadOnlyList<InstalledUpdate> CorrelatedUpdates,
    bool HardwareConfirmed,
    CrashEvent? FirstCrash)
{
    public bool HasCrashes => Data.HasCrashes;

    /// <summary><c>true</c> si la hipótesis "fue una actualización" tiene respaldo en las fechas.</summary>
    public bool UpdateHypothesisSupported => CorrelatedUpdates.Count > 0 && !HardwareConfirmed;
}

/// <summary>
/// Analiza los datos de inestabilidad. Lógica pura sobre <see cref="CrashData"/>: se testea entera
/// sin Windows.
/// </summary>
public static class CrashAnalyzer
{
    /// <summary>Ventana para considerar que una actualización pudo causar un pantallazo.</summary>
    public static readonly TimeSpan CorrelationWindow = TimeSpan.FromDays(7);

    public static CrashAnalysis Analyze(CrashData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        Dictionary<CrashSuspect, int> counts = data.Crashes
            .GroupBy(c => c.BugCheck.Suspect)
            .ToDictionary(g => g.Key, g => g.Count());

        // El dominante es el más frecuente; ante empate gana el de categoría más grave, porque en la
        // duda conviene revisar hardware antes que software.
        CrashSuspect dominant = counts.Count == 0
            ? CrashSuspect.Unknown
            : counts
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => Gravity(kv.Key))
                .First().Key;

        // WHEA es evidencia directa del firmware: manda sobre cualquier otra hipótesis.
        bool hardwareConfirmed =
            data.WheaErrorCount > 0 ||
            data.Crashes.Any(c => c.BugCheck.Suspect == CrashSuspect.Hardware);

        CrashEvent? first = data.Crashes.Count == 0
            ? null
            : data.Crashes.OrderBy(c => c.When).First();

        var correlated = data.RecentUpdates
            .Where(u => data.Crashes.Any(c =>
                u.InstalledOn <= c.When && u.InstalledOn >= c.When - CorrelationWindow))
            .OrderByDescending(u => u.InstalledOn)
            .ToList();

        return new CrashAnalysis(data, dominant, counts, correlated, hardwareConfirmed, first);
    }

    /// <summary>Convierte el análisis en hallazgos para el reporte.</summary>
    public static IReadOnlyList<Finding> ToFindings(CrashAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (!analysis.HasCrashes && analysis.Data.WheaErrorCount == 0)
        {
            return Array.Empty<Finding>();
        }

        var findings = new List<Finding>();
        CrashData data = analysis.Data;

        // --- WHEA primero: si el hardware reporta fallas, todo lo demás es secundario ---
        if (data.WheaErrorCount > 0)
        {
            findings.Add(new Finding(
                "crash.whea",
                Severity.Critical,
                "El hardware está reportando fallas",
                $"Hay {data.WheaErrorCount} error(es) WHEA en los últimos {data.WindowDays} días. WHEA " +
                "es el mecanismo por el que el propio hardware le avisa a Windows que falló: procesador, " +
                "memoria, placa o un dispositivo PCIe. Ninguna reparación de software arregla esto, y " +
                "no lo explica ninguna actualización.",
                new[] { new Metric("Errores WHEA", data.WheaErrorCount.ToString(Es)) }));
        }

        // --- El resumen de pantallazos ---
        if (data.Crashes.Count > 0)
        {
            BugCheck top = analysis.DominantSuspect == CrashSuspect.Unknown
                ? data.Crashes[0].BugCheck
                : data.Crashes.First(c => c.BugCheck.Suspect == analysis.DominantSuspect).BugCheck;

            var metrics = new List<Metric>
            {
                new("Pantallazos", data.Crashes.Count.ToString(Es), $"en {data.WindowDays} días"),
                new("Código más frecuente", top.Code),
            };

            if (analysis.FirstCrash is { } first)
            {
                metrics.Add(new Metric("El primero fue", first.When.ToString("yyyy-MM-dd HH:mm", Es)));
            }

            findings.Add(new Finding(
                "crash.bluescreen",
                Severity.Critical,
                $"{data.Crashes.Count} pantallazo(s) azul(es) en {data.WindowDays} días",
                $"El código más frecuente es {top.Code} ({top.Name}). {top.Meaning} " +
                SuspectAdvice(analysis.DominantSuspect),
                metrics,
                SuggestedFixesFor(analysis.DominantSuspect)));
        }
        else if (data.MinidumpCount > 0)
        {
            // Hay volcados pero no eventos: el registro pudo haberse limpiado.
            findings.Add(new Finding(
                "crash.dumps-only",
                Severity.Warning,
                $"Hay {data.MinidumpCount} volcado(s) de memoria pero ningún evento de pantallazo",
                "Los archivos de volcado indican que el equipo se cayó, pero el registro de eventos no " +
                "tiene los pantallazos correspondientes: pudo haberse limpiado. Los volcados siguen " +
                "sirviendo para identificar el driver responsable.",
                new[] { new Metric("Volcados", data.MinidumpCount.ToString(Es)) }));
        }

        // --- La correlación con actualizaciones ---
        if (data.Crashes.Count > 0 && data.RecentUpdates.Count > 0)
        {
            if (analysis.CorrelatedUpdates.Count > 0)
            {
                string list = string.Join(", ", analysis.CorrelatedUpdates.Take(4).Select(u => u.Id));
                string detail =
                    $"Hay pantallazos dentro de los 7 días posteriores a instalar: {list}. " +
                    "Coincidir en el tiempo no es causar, pero es la primera pista a seguir.";

                if (analysis.HardwareConfirmed)
                {
                    detail +=
                        " OJO: también hay evidencia de falla de hardware, así que lo más probable es " +
                        "que la actualización solo haya destapado un problema que ya existía. " +
                        "Desinstalarla puede callar el síntoma sin resolver la causa.";
                }
                else if (analysis.DominantSuspect is CrashSuspect.Memory or CrashSuspect.Disk)
                {
                    detail +=
                        $" Pero el código de parada apunta a {SuspectName(analysis.DominantSuspect)}: " +
                        "conviene descartar eso antes de desinstalar la actualización.";
                }

                findings.Add(new Finding(
                    "crash.update-correlation",
                    Severity.Warning,
                    "Los pantallazos coinciden con actualizaciones recientes",
                    detail,
                    new[] { new Metric("Actualizaciones sospechosas", analysis.CorrelatedUpdates.Count.ToString(Es)) },
                    new[] { "update.remove" }));
            }
            else
            {
                // Descartar una hipótesis también es información, y ahorra horas.
                findings.Add(new Finding(
                    "crash.no-update-correlation",
                    Severity.Info,
                    "Los pantallazos NO coinciden con las actualizaciones",
                    $"Ningún pantallazo cae dentro de los 7 días posteriores a una actualización, sobre " +
                    $"{data.RecentUpdates.Count} instalada(s) en la ventana. La hipótesis de que los " +
                    "causó una actualización no se sostiene con estas fechas: conviene mirar hardware " +
                    "o un driver de terceros.",
                    new[] { new Metric("Actualizaciones en la ventana", data.RecentUpdates.Count.ToString(Es)) }));
            }
        }

        // --- Señales de apoyo ---
        if (data.DiskErrorCount > 0)
        {
            findings.Add(new Finding(
                "crash.disk-errors",
                Severity.Critical,
                $"{data.DiskErrorCount} error(es) de disco en el registro de eventos",
                "Un disco con errores de lectura o escritura causa pantallazos por sí solo, y también " +
                "corrompe archivos del sistema. Es de las primeras cosas a descartar.",
                new[] { new Metric("Errores de disco", data.DiskErrorCount.ToString(Es)) },
                new[] { "disk.chkdsk" }));
        }

        // Más apagones que pantallazos significa que el equipo se apaga SIN llegar a un BSOD: eso
        // apunta a fuente, calor o RAM, no a software.
        if (data.UnexpectedShutdownCount > data.Crashes.Count && data.UnexpectedShutdownCount > 1)
        {
            findings.Add(new Finding(
                "crash.unexpected-shutdowns",
                Severity.Warning,
                $"{data.UnexpectedShutdownCount} apagones sin cierre limpio",
                "Hay más apagones inesperados que pantallazos, así que el equipo se está apagando sin " +
                "llegar a mostrar el error. Eso apunta a la fuente de alimentación, sobrecalentamiento " +
                "o memoria — no a un problema de software.",
                new[] { new Metric("Apagones inesperados", data.UnexpectedShutdownCount.ToString(Es)) }));
        }

        if (data.ProblemDeviceNames.Count > 0)
        {
            findings.Add(new Finding(
                "crash.problem-devices",
                Severity.Warning,
                $"{data.ProblemDeviceNames.Count} dispositivo(s) con problema",
                "Estos dispositivos tienen el driver ausente o con error, y un driver roto es una causa " +
                "habitual de pantallazos: " + string.Join(", ", data.ProblemDeviceNames.Take(5)) + ".",
                new[] { new Metric("Dispositivos con problema", data.ProblemDeviceNames.Count.ToString(Es)) }));
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.CheckId, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private static int Gravity(CrashSuspect suspect) => suspect switch
    {
        CrashSuspect.Hardware => 5,
        CrashSuspect.Memory => 4,
        CrashSuspect.Disk => 3,
        CrashSuspect.SystemFiles => 2,
        CrashSuspect.GraphicsDriver => 1,
        CrashSuspect.Driver => 1,
        _ => 0,
    };

    private static string SuspectName(CrashSuspect suspect) => suspect switch
    {
        CrashSuspect.Memory => "la memoria RAM",
        CrashSuspect.Disk => "el disco",
        CrashSuspect.Driver => "un driver",
        CrashSuspect.GraphicsDriver => "el driver de video",
        CrashSuspect.SystemFiles => "los archivos del sistema",
        CrashSuspect.Hardware => "el hardware",
        _ => "una causa no identificada",
    };

    private static string SuspectAdvice(CrashSuspect suspect) => suspect switch
    {
        CrashSuspect.Memory =>
            "EasyFix puede programar el diagnóstico de memoria de Windows para el próximo reinicio, " +
            "pero si la RAM está mal hay que reemplazarla: no se arregla con software.",

        CrashSuspect.Disk =>
            "EasyFix revisa el disco y programa la reparación del sistema de archivos. Si el disco " +
            "está fallando físicamente, hay que reemplazarlo.",

        CrashSuspect.SystemFiles =>
            "Esto sí se repara: EasyFix restaura los archivos del sistema contra la imagen de " +
            "referencia de Windows.",

        CrashSuspect.GraphicsDriver =>
            "Conviene revertir el driver de video a la versión anterior, o instalar el del fabricante " +
            "del equipo en lugar del genérico de Windows Update.",

        CrashSuspect.Driver =>
            "Hay que identificar el driver en el volcado de memoria y revertirlo. EasyFix repara los " +
            "archivos del sistema y revisa el disco, que descarta las otras causas.",

        CrashSuspect.Hardware =>
            "Es hardware. Ninguna reparación de software lo resuelve.",

        _ =>
            "No se pudo determinar la categoría. Hace falta analizar el volcado de memoria.",
    };

    private static string[] SuggestedFixesFor(CrashSuspect suspect) => suspect switch
    {
        CrashSuspect.Memory => new[] { "memory.schedule-test" },
        CrashSuspect.Disk => new[] { "disk.chkdsk", "system.repair-files" },
        CrashSuspect.SystemFiles => new[] { "system.repair-files" },
        CrashSuspect.Driver or CrashSuspect.GraphicsDriver => new[] { "system.repair-files", "disk.chkdsk" },
        CrashSuspect.Hardware => Array.Empty<string>(),
        _ => new[] { "system.repair-files", "disk.chkdsk" },
    };
}
