using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Diagnostics;

/// <summary>Identidad del equipo, para el encabezado de la pantalla y del informe.</summary>
public sealed record MachineIdentity
{
    public string? OsCaption { get; init; }
    public string? OsBuild { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }

    /// <summary>Resumen de dos líneas para el encabezado. Nunca tira excepción por datos faltantes.</summary>
    public string Describe(SystemSnapshot s)
    {
        var line1 = new List<string>();
        if (OsCaption is not null) { line1.Add(OsCaption.Replace("Microsoft ", string.Empty, StringComparison.Ordinal)); }
        if (Model is not null) { line1.Add(Model); }
        if (s.CpuName is not null) { line1.Add(ShortenCpu(s.CpuName)); }

        var line2 = new List<string>();
        if (s.TotalRamGb is double ram) { line2.Add($"{ram:0.#} GB RAM"); }

        string disk = s.PrimaryDiskMedia switch
        {
            DiskMedia.Hdd => "Disco mecánico",
            DiskMedia.Ssd => "SSD",
            DiskMedia.Scm => "Optane",
            _ => "Disco no identificado",
        };
        if (s.SystemDriveTotalGb is double total) { disk += $" {total:0} GB"; }
        line2.Add(disk);

        if (s.SystemDriveFreePercent is double free && s.SystemDriveTotalGb is double t)
        {
            line2.Add($"{t * free / 100:0} GB libres");
        }

        string first = line1.Count > 0 ? string.Join(" · ", line1) : "Equipo no identificado";
        return line2.Count > 0 ? $"{first}\n{string.Join(" · ", line2)}" : first;
    }

    /// <summary>"Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz" → "i5-8250U".</summary>
    private static string ShortenCpu(string name)
    {
        string cleaned = name
            .Replace("(R)", string.Empty, StringComparison.Ordinal)
            .Replace("(TM)", string.Empty, StringComparison.Ordinal)
            .Replace("CPU", string.Empty, StringComparison.Ordinal);

        int at = cleaned.IndexOf('@', StringComparison.Ordinal);
        if (at > 0) { cleaned = cleaned[..at]; }

        string[] words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // El modelo es la palabra que mezcla letras y dígitos: i5-8250U, 5600G, N4020.
        string? model = words.FirstOrDefault(w => w.Any(char.IsDigit) && w.Any(char.IsLetter));
        return model ?? cleaned.Trim();
    }
}

/// <param name="Snapshot">Lo que se pudo medir.</param>
/// <param name="Identity">Identidad del equipo.</param>
/// <param name="Failures">
/// Sondas que no se pudieron leer, con el motivo. Se muestran como "no determinado": una sonda que
/// falló no es evidencia de que el equipo esté sano.
/// </param>
/// <summary>Avance del análisis, para poder mostrar una barra que llega al final.</summary>
/// <param name="Completed">Sondas terminadas.</param>
/// <param name="Total">Sondas totales.</param>
/// <param name="Current">Qué se está midiendo ahora, en lenguaje de usuario.</param>
public sealed record ProbeProgress(int Completed, int Total, string Current)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100.0 * Completed / Total, 0, 100);
}

/// <param name="Crash">Datos de inestabilidad: pantallazos, WHEA, apagones, actualizaciones.</param>
public sealed record ProbeResult(
    SystemSnapshot Snapshot,
    MachineIdentity Identity,
    IReadOnlyList<CheckFailure> Failures,
    CrashData Crash);

/// <summary>
/// La única clase que habla con Windows. Lee WMI, el registro y el Event Log; no modifica nada.
/// </summary>
/// <remarks>
/// <para><b>Por qué está todo junto acá.</b> Los contratos de WMI y del Event Log no se pueden
/// verificar sin un Windows real: los nombres de clase, los valores de <c>MediaType</c> y los campos
/// del evento 100 salen de la documentación. Concentrarlos en un archivo tiene dos consecuencias
/// buenas: cuando aparezca un contrato equivocado hay un solo lugar donde corregirlo, y todo el resto
/// del diagnóstico —las reglas que deciden qué es un problema— queda como lógica pura sobre
/// <see cref="SystemSnapshot"/>, testeable desde cualquier sistema.</para>
///
/// <para><b>Cada sonda falla sola.</b> Una clase WMI que no existe en esa versión de Windows, o una
/// consulta sin permisos, deja su campo en <c>null</c> y agrega una entrada a
/// <see cref="ProbeResult.Failures"/>. Nunca tumba el resto. Un <c>null</c> significa "no se pudo
/// medir", jamás "cero".</para>
///
/// <para><b>Sin contadores de rendimiento por nombre.</b> <c>\Memory\Committed Bytes</c> no existe en
/// un Windows en español, donde se llama <c>\Memoria\Bytes confirmados</c>. Se usan las clases
/// <c>Win32_PerfFormattedData_*</c>, cuyas propiedades tienen el mismo nombre en cualquier idioma.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemProbe
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string WmiNamespace = @"root\wmi";
    private const string SecurityCenterNamespace = @"root\SecurityCenter2";
    private const string BitLockerNamespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    private const string PerformanceLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    private static readonly string[] RunKeys =
    {
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
    };

    /// <summary>
    /// Sondas que necesitan más tiempo que el resto.
    /// </summary>
    /// <remarks>
    /// Las clases <c>Win32_PerfFormattedData_*</c> tienen que inicializar el subsistema de contadores
    /// de rendimiento en la primera consulta, y eso tarda más de 5 segundos. En los dos equipos
    /// probados <c>disk.latency</c> y <c>memory.pressure</c> dieron timeout siempre, así que esos dos
    /// datos nunca llegaban al reporte.
    /// </remarks>
    private static readonly Dictionary<string, TimeSpan> SlowProbes = new(StringComparer.Ordinal)
    {
        ["disk.latency"] = TimeSpan.FromSeconds(25),
        ["memory.pressure"] = TimeSpan.FromSeconds(25),

        // Estas dos dieron timeout en un Pentium G2020 con Windows 11: WMI Storage y el Event Log
        // son lentos en equipos de gama baja, que son justo los que más se reparan.
        ["disk.physical"] = TimeSpan.FromSeconds(30),
        ["crash"] = TimeSpan.FromSeconds(40),
    };

    private readonly ILogger<SystemProbe> _logger;
    private readonly TimeSpan _perProbeTimeout;

    public SystemProbe(ILogger<SystemProbe> logger, TimeSpan? perProbeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _perProbeTimeout = perProbeTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Corre todas las sondas en paralelo y devuelve lo que se pudo medir.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(
        IProgress<ProbeProgress>? progress = null, CancellationToken ct = default)
    {
        var snapshot = new SystemSnapshot();
        var failures = new List<CheckFailure>();
        var sync = new object();

        // Cada sonda devuelve una función que aplica su resultado sobre el snapshot. Así corren en
        // paralelo sin compartir estado mutable y el merge queda en un solo lugar.
        var probes = new (string Id, string Label, Func<Func<SystemSnapshot, SystemSnapshot>> Run)[]
        {
            ("os", "Identificando el equipo", ProbeOperatingSystem),
            ("cpu", "Procesador", ProbeCpu),
            ("memory", "Memoria instalada", ProbeMemory),
            ("memory.pressure", "Uso de memoria", ProbeMemoryPressure),
            ("disk.logical", "Espacio libre en disco", ProbeLogicalDisk),
            ("disk.physical", "Tipo y salud del disco", ProbePhysicalDisk),
            ("disk.smart", "Estado SMART del disco", ProbeSmart),
            ("disk.latency", "Velocidad de respuesta del disco", ProbeDiskLatency),
            ("startup", "Programas de arranque", ProbeStartupEntries),
            ("boot", "Tiempo de encendido", ProbeBootTime),
            ("antivirus", "Antivirus instalados", ProbeAntivirus),
            ("bitlocker", "Cifrado BitLocker", ProbeBitLocker),
            ("printers", "Impresoras", ProbePrinters),
            ("bluetooth", "Bluetooth", ProbeBluetooth),
            ("crash", "Pantallazos azules", ProbeCrashData),
        };

        using var gate = new SemaphoreSlim(6);

        int completed = 0;
        int total = probes.Length;

        IEnumerable<Task<Func<SystemSnapshot, SystemSnapshot>?>> tasks = probes.Select(async probe =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                progress?.Report(new ProbeProgress(completed, total, probe.Label));

                return await RunProbeAsync(probe.Id, probe.Run, failures, sync, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();

                // El contador avanza al terminar, no al empezar: con sondas en paralelo, contar al
                // arrancar haría que la barra llegue al 100 % con trabajo todavía en curso.
                progress?.Report(new ProbeProgress(Interlocked.Increment(ref completed), total, probe.Label));
            }
        });

        Func<SystemSnapshot, SystemSnapshot>?[] mutations = await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (Func<SystemSnapshot, SystemSnapshot>? mutate in mutations)
        {
            if (mutate is not null)
            {
                snapshot = mutate(snapshot);
            }
        }

        return new ProbeResult(
            snapshot,
            _identity,
            failures.OrderBy(f => f.CheckId, StringComparer.Ordinal).ToList(),
            _crash);
    }

    private async Task<Func<SystemSnapshot, SystemSnapshot>?> RunProbeAsync(
        string id,
        Func<Func<SystemSnapshot, SystemSnapshot>> run,
        List<CheckFailure> failures,
        object sync,
        CancellationToken ct)
    {
        try
        {
            // Las APIs de WMI son sincrónicas y bloqueantes: van a un hilo del pool para no
            // congelar la interfaz, con timeout propio.
            TimeSpan budget = SlowProbes.TryGetValue(id, out TimeSpan slow) ? slow : _perProbeTimeout;

            Task<Func<SystemSnapshot, SystemSnapshot>> work = Task.Run(run, ct);
            Task finished = await Task.WhenAny(work, Task.Delay(budget, ct)).ConfigureAwait(false);

            if (finished != work)
            {
                // La consulta sigue corriendo en su hilo; se abandona. Es el caso de SMART colgado
                // en discos que están fallando, que es justo cuando más importa no bloquearse.
                Record(failures, sync, new CheckFailure(
                    id, $"No se pudo determinar: tardó más de {budget.TotalSeconds:0} s.", true));
                return null;
            }

            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "La sonda {ProbeId} falló.", id);
            Record(failures, sync, new CheckFailure(
                id, $"No se pudo determinar: {ex.GetType().Name}: {ex.Message}", false));
            return null;
        }
    }

    private static void Record(List<CheckFailure> failures, object sync, CheckFailure failure)
    {
        lock (sync)
        {
            failures.Add(failure);
        }
    }

    // ---- Sondas -----------------------------------------------------------------------------

    /// <summary>
    /// La identidad del equipo no cabe en <see cref="SystemSnapshot"/>, así que esta sonda la deja
    /// acá. Se escribe una sola vez, desde el hilo de esta sonda, y se lee después de
    /// <c>Task.WhenAll</c>: no hay carrera.
    /// </summary>
    private volatile MachineIdentity _identity = new();

    /// <summary>
    /// Los datos de inestabilidad no caben en <see cref="SystemSnapshot"/>. Misma mecánica que
    /// <see cref="_identity"/>: se escribe desde el hilo de su sonda y se lee tras <c>Task.WhenAll</c>.
    /// </summary>
    private volatile CrashData _crash = CrashData.Empty;

    /// <summary>Días de historial que se analizan para los pantallazos.</summary>
    public int CrashWindowDays { get; init; } = 60;

    private Func<SystemSnapshot, SystemSnapshot> ProbeOperatingSystem()
    {
        string? caption = null, build = null, manufacturer = null, model = null;
        bool domainJoined = false;

        foreach (ManagementObject os in Query("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
        {
            caption = Text(os, "Caption");
            build = Text(os, "BuildNumber");
            break;
        }

        foreach (ManagementObject cs in Query(
            "SELECT Manufacturer, Model, PartOfDomain FROM Win32_ComputerSystem"))
        {
            manufacturer = Text(cs, "Manufacturer");
            model = Text(cs, "Model");
            domainJoined = Bool(cs, "PartOfDomain") ?? false;
            break;
        }

        _identity = new MachineIdentity
        {
            OsCaption = caption,
            OsBuild = build,
            Manufacturer = manufacturer,
            Model = model,
        };

        return s => s with { IsDomainJoined = domainJoined };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeCpu()
    {
        string? name = null;
        int? cores = null;
        double? percentOfMax = null;

        foreach (ManagementObject cpu in Query(
            "SELECT Name, NumberOfCores, MaxClockSpeed, CurrentClockSpeed FROM Win32_Processor"))
        {
            name = Text(cpu, "Name");
            cores = (int?)Number(cpu, "NumberOfCores");

            double? max = Number(cpu, "MaxClockSpeed");
            double? current = Number(cpu, "CurrentClockSpeed");
            if (max > 0 && current > 0)
            {
                percentOfMax = 100.0 * current.Value / max.Value;
            }

            break; // el primer socket alcanza
        }

        return s => s with
        {
            CpuName = name,
            CpuCoreCount = cores,
            CpuPercentOfMaxFrequency = percentOfMax,
        };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeMemory()
    {
        double totalBytes = 0;
        int modules = 0;
        string? type = null;
        int? speed = null;

        foreach (ManagementObject module in Query(
            "SELECT Capacity, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            totalBytes += Number(module, "Capacity") ?? 0;
            modules++;
            type ??= MemoryTypeName(Number(module, "SMBIOSMemoryType"));
            speed ??= (int?)Number(module, "Speed");
        }

        int? freeSlots = null;
        foreach (ManagementObject array in Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
        {
            if (Number(array, "MemoryDevices") is double slots && slots >= modules)
            {
                freeSlots = (int)slots - modules;
            }

            break;
        }

        double? totalGb = totalBytes > 0 ? totalBytes / (1024 * 1024 * 1024) : null;

        return s => s with
        {
            TotalRamGb = totalGb,
            FreeMemorySlots = freeSlots,
            MemoryType = type,
            MemorySpeedMhz = speed,
        };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeMemoryPressure()
    {
        double? percent = null;

        foreach (ManagementObject m in Query(
            "SELECT CommittedBytes, CommitLimit FROM Win32_PerfFormattedData_PerfOS_Memory"))
        {
            double? committed = Number(m, "CommittedBytes");
            double? limit = Number(m, "CommitLimit");
            if (committed is not null && limit > 0)
            {
                percent = 100.0 * committed.Value / limit.Value;
            }

            break;
        }

        return s => s with { CommitUsedPercent = percent };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeLogicalDisk()
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        double? freePercent = null, totalGb = null;

        foreach (ManagementObject d in Query(
            $"SELECT FreeSpace, Size FROM Win32_LogicalDisk WHERE DeviceID = '{systemDrive}'"))
        {
            double? free = Number(d, "FreeSpace");
            double? size = Number(d, "Size");
            if (free is not null && size > 0)
            {
                freePercent = 100.0 * free.Value / size.Value;
                totalGb = size.Value / (1024 * 1024 * 1024);
            }

            break;
        }

        return s => s with { SystemDriveFreePercent = freePercent, SystemDriveTotalGb = totalGb };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbePhysicalDisk()
    {
        DiskMedia media = DiskMedia.Unknown;
        DiskHealth health = DiskHealth.Unknown;

        foreach (ManagementObject disk in Query(
            "SELECT MediaType, BusType, HealthStatus, SpindleSpeed FROM MSFT_PhysicalDisk",
            StorageNamespace))
        {
            media = (int?)Number(disk, "MediaType") switch
            {
                3 => DiskMedia.Hdd,
                4 => DiskMedia.Ssd,
                5 => DiskMedia.Scm,
                _ => DiskMedia.Unknown,
            };

            // Plan B: varios controladores RAID informan MediaType = 0. SpindleSpeed 0 es SSD;
            // cualquier valor mayor son las RPM reales de un disco mecánico.
            if (media == DiskMedia.Unknown && Number(disk, "SpindleSpeed") is double rpm)
            {
                media = rpm switch
                {
                    0 => DiskMedia.Ssd,
                    > 1 => DiskMedia.Hdd,
                    _ => DiskMedia.Unknown,
                };
            }

            health = (int?)Number(disk, "HealthStatus") switch
            {
                0 => DiskHealth.Healthy,
                1 => DiskHealth.Warning,
                2 => DiskHealth.Failing,
                _ => DiskHealth.Unknown,
            };

            break; // el primero es el disco del sistema en la enorme mayoría de los equipos
        }

        return s => s with { PrimaryDiskMedia = media, PrimaryDiskHealth = health };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeSmart()
    {
        bool? predictsFailure = null;

        foreach (ManagementObject status in Query(
            "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus", WmiNamespace))
        {
            if (Bool(status, "PredictFailure") == true)
            {
                predictsFailure = true;
                break;
            }

            predictsFailure = false;
        }

        return s => predictsFailure switch
        {
            // SMART manda sobre HealthStatus: es la señal más directa de falla inminente.
            true => s with { PrimaryDiskHealth = DiskHealth.Failing },
            false when s.PrimaryDiskHealth == DiskHealth.Unknown => s with { PrimaryDiskHealth = DiskHealth.Healthy },
            _ => s,
        };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeDiskLatency()
    {
        double? milliseconds = null;

        foreach (ManagementObject d in Query(
            "SELECT AvgDisksecPerTransfer FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk " +
            "WHERE Name = '_Total'"))
        {
            // El contador está en unidades de 100 ns dentro de la clase formateada: llega ya en ms
            // en la mayoría de los equipos, pero puede venir en segundos. Se normaliza: un valor
            // menor a 1 casi seguro son segundos.
            if (Number(d, "AvgDisksecPerTransfer") is double raw && raw > 0)
            {
                milliseconds = raw < 1 ? raw * 1000 : raw;
            }

            // Un cero exacto significa que el contador todavía no acumuló muestras, no que el disco
            // responda en cero. Se deja como no medido: un dato inventado es peor que uno ausente.

            break;
        }

        return s => s with { DiskLatencyMs = milliseconds };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeStartupEntries()
    {
        int enabled = 0;

        foreach (string key in RunKeys)
        {
            enabled += OpenNames(key)?.Length ?? 0;
        }

        // Carpetas de inicio: usuario actual y todos los usuarios.
        foreach (Environment.SpecialFolder folder in new[]
                 { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                enabled += Directory.EnumerateFiles(path)
                    .Count(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase));
            }
        }

        return s => s with { EnabledStartupEntryCount = enabled };

        static string[]? OpenNames(string fullKey)
        {
            int slash = fullKey.IndexOf('\\', StringComparison.Ordinal);
            string hive = fullKey[..slash];
            string sub = fullKey[(slash + 1)..];

            using RegistryKey? root = hive switch
            {
                "HKEY_CURRENT_USER" => Registry.CurrentUser.OpenSubKey(sub),
                "HKEY_LOCAL_MACHINE" => Registry.LocalMachine.OpenSubKey(sub),
                _ => null,
            };

            return root?.GetValueNames();
        }
    }

    /// <summary>
    /// Lee el tiempo del último arranque y cuánto lo retrasaron los programas de inicio.
    /// </summary>
    /// <remarks>
    /// <para><b>Dos correcciones respecto de la primera versión</b>, que en un equipo real reportó
    /// «814 s de retraso» sobre un arranque de 27 s — un número absurdo que destruye la credibilidad
    /// de todo el reporte:</para>
    ///
    /// <list type="number">
    /// <item>Se usa <c>DegradationTime</c>, no <c>TotalTime</c>. <c>TotalTime</c> es lo que tardó la
    /// app en total; la <i>degradación</i> es cuánto de eso retrasó el arranque. Sumar totales cuenta
    /// tiempo que ocurrió en paralelo.</item>
    /// <item>Solo se cuentan los eventos <b>del último arranque</b>. Antes se sumaban hasta 40
    /// eventos de todo el historial, así que el número crecía con la antigüedad del equipo.</item>
    /// </list>
    /// </remarks>
    private static Func<SystemSnapshot, SystemSnapshot> ProbeBootTime()
    {
        int? mainPath = null;
        DateTime? lastBoot = null;

        var query = new EventLogQuery(PerformanceLog, PathType.LogName, "*[System[(EventID=100)]]")
        {
            ReverseDirection = true, // el más reciente primero
        };

        using (var reader = new EventLogReader(query))
        {
            using EventRecord? record = reader.ReadEvent();
            if (record is not null)
            {
                mainPath = EventDataInt(record, "MainPathBootTime");
                lastBoot = record.TimeCreated;
            }
        }

        // Sin la marca del último arranque no se puede acotar la ventana, y sumar todo el historial
        // da un número sin sentido. Se prefiere no informar antes que informar mal.
        int? degradation = null;

        if (lastBoot is DateTime bootTime)
        {
            var degradationQuery = new EventLogQuery(
                PerformanceLog, PathType.LogName, "*[System[(EventID=101 or EventID=103)]]")
            {
                ReverseDirection = true,
            };

            int total = 0;
            int counted = 0;
            int inspected = 0;

            using var reader = new EventLogReader(degradationQuery);

            while (inspected < 200)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null) { break; }

                inspected++;

                if (record.TimeCreated is not DateTime when)
                {
                    continue;
                }

                // Van del más nuevo al más viejo. Los eventos del arranque llegan poco después del
                // evento 100; en cuanto se cruza esa marca, el resto es de arranques anteriores.
                if (when < bootTime)
                {
                    break;
                }

                if (EventDataInt(record, "DegradationTime") is int ms && ms > 0)
                {
                    total += ms;
                    counted++;
                }
            }

            if (counted > 0)
            {
                degradation = total;
            }
        }

        return s => s with { MainPathBootTimeMs = mainPath, StartupDegradationMs = degradation };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeAntivirus()
    {
        int active = 0;

        foreach (ManagementObject av in Query(
            "SELECT displayName, productState FROM AntiVirusProduct", SecurityCenterNamespace))
        {
            // productState es un mapa de bits; el byte del medio distinto de 0x00 significa que la
            // protección en tiempo real está encendida.
            if (Number(av, "productState") is double state && ((int)state >> 8 & 0xFF) != 0)
            {
                active++;
            }
        }

        return s => s with { ActiveAntivirusCount = active };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeBitLocker()
    {
        bool active = false;

        foreach (ManagementObject volume in Query(
            "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", BitLockerNamespace))
        {
            // ProtectionStatus: 0 = apagado, 1 = encendido, 2 = desconocido.
            if (Number(volume, "ProtectionStatus") is double status && (int)status == 1)
            {
                active = true;
                break;
            }
        }

        return s => s with { BitLockerActive = active };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbePrinters()
    {
        bool any = false;

        foreach (ManagementObject printer in Query("SELECT Name FROM Win32_Printer"))
        {
            string? name = Text(printer, "Name");
            // Las impresoras virtuales que trae Windows no cuentan: no romper el spooler por ellas
            // es el punto, pero tampoco protegerlo por una que nadie usa.
            if (name is not null &&
                !name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Microsoft Print to PDF", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Microsoft XPS", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Fax", StringComparison.OrdinalIgnoreCase))
            {
                any = true;
                break;
            }
        }

        return s => s with { HasPrinters = any };
    }

    private static Func<SystemSnapshot, SystemSnapshot> ProbeBluetooth()
    {
        bool any = false;

        foreach (ManagementObject _ in Query(
            "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'"))
        {
            any = true;
            break;
        }

        return s => s with { HasBluetoothAdapter = any };
    }

    /// <summary>
    /// Lee todo lo que indica inestabilidad: pantallazos, errores de hardware reportados por el
    /// firmware, apagones sucios, errores de disco, volcados y actualizaciones recientes.
    /// </summary>
    /// <remarks>
    /// Cada bloque va en su propio try: en un equipo que se cae, es habitual que algún registro esté
    /// corrupto o vacío. Que falte uno no puede impedir leer los demás.
    /// </remarks>
    private Func<SystemSnapshot, SystemSnapshot> ProbeCrashData()
    {
        DateTime since = DateTime.Now.AddDays(-CrashWindowDays);

        var crashes = new List<CrashEvent>();
        int whea = 0, shutdowns = 0, diskErrors = 0, minidumps = 0;
        var updates = new List<InstalledUpdate>();
        var problemDevices = new List<string>();

        // --- Pantallazos: evento 1001 de WER-SystemErrorReporting trae el bugcheck en el mensaje ---
        try
        {
            var query = new EventLogQuery(
                "System", PathType.LogName, "*[System[(EventID=1001)]]") { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int read = 0;

            while (read < 200)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null) { break; }

                read++;
                if (record.TimeCreated is not DateTime when || when < since) { continue; }

                // Solo los de bugcheck: el 1001 lo usan varios proveedores.
                string? provider = record.ProviderName;
                if (provider is not null &&
                    !provider.Contains("SystemErrorReporting", StringComparison.OrdinalIgnoreCase) &&
                    !provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                crashes.Add(new CrashEvent(new DateTimeOffset(when), ExtractBugCheckCode(record)));
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Sin acceso al registro de eventos no hay análisis de pantallazos, pero el resto sigue.
        }

        whea = CountEvents("Microsoft-Windows-WHEA-Logger", null, since);
        shutdowns = CountEvents("Microsoft-Windows-Kernel-Power", 41, since);

        foreach (string provider in new[] { "disk", "Ntfs", "volmgr" })
        {
            diskErrors += CountEvents(provider, null, since, onlyErrors: true);
        }

        // --- Volcados de memoria ---
        try
        {
            string minidumpDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

            if (Directory.Exists(minidumpDir))
            {
                minidumps = Directory.EnumerateFiles(minidumpDir, "*.dmp").Count();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // --- Actualizaciones instaladas, para el cruce de fechas ---
        try
        {
            foreach (ManagementObject hotfix in Query(
                "SELECT HotFixID, InstalledOn, Description FROM Win32_QuickFixEngineering"))
            {
                string? id = Text(hotfix, "HotFixID");
                string? installedOn = Text(hotfix, "InstalledOn");

                if (id is null || installedOn is null) { continue; }

                // InstalledOn viene como texto y su formato depende de la configuración regional.
                if (!DateTime.TryParse(installedOn, CultureInfo.CurrentCulture,
                        DateTimeStyles.None, out DateTime when) &&
                    !DateTime.TryParse(installedOn, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out when))
                {
                    continue;
                }

                if (when >= since)
                {
                    updates.Add(new InstalledUpdate(id, new DateTimeOffset(when), Text(hotfix, "Description")));
                }
            }
        }
        catch (ManagementException)
        {
        }

        // --- Dispositivos con driver roto o ausente ---
        try
        {
            foreach (ManagementObject device in Query(
                "SELECT Name FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            {
                if (Text(device, "Name") is { } name)
                {
                    problemDevices.Add(name);
                }
            }
        }
        catch (ManagementException)
        {
        }

        _crash = new CrashData(
            crashes.OrderByDescending(c => c.When).ToList(),
            minidumps,
            whea,
            shutdowns,
            diskErrors,
            updates.OrderByDescending(u => u.InstalledOn).ToList(),
            problemDevices,
            CrashWindowDays);

        // Los pantallazos no modifican el snapshot: viajan en ProbeResult.Crash.
        return s => s;
    }

    /// <summary>
    /// Extrae el código de parada del mensaje del evento. El texto está localizado, así que se busca
    /// el patrón hexadecimal en vez de una frase.
    /// </summary>
    private static string? ExtractBugCheckCode(EventRecord record)
    {
        string? message;
        try
        {
            message = record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(
                message,
                @"0x[0-9a-fA-F]{8}",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(2));

        return match.Success ? match.Value : null;
    }

    /// <summary>Cuenta eventos de un proveedor desde una fecha. Devuelve 0 si el registro no existe.</summary>
    private static int CountEvents(string providerName, int? eventId, DateTime since, bool onlyErrors = false)
    {
        try
        {
            string filter = $"*[System[Provider[@Name='{providerName}']";
            if (eventId is int id) { filter += $" and (EventID={id})"; }
            if (onlyErrors) { filter += " and (Level=1 or Level=2)"; }
            filter += "]]";

            var query = new EventLogQuery("System", PathType.LogName, filter) { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            int inspected = 0;

            while (inspected < 500)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null) { break; }

                inspected++;
                if (record.TimeCreated is DateTime when && when >= since)
                {
                    count++;
                }
                else if (record.TimeCreated is not null)
                {
                    // Van del más nuevo al más viejo: al pasar la ventana no hace falta seguir.
                    break;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ---- Helpers de lectura -----------------------------------------------------------------

    private static IEnumerable<ManagementObject> Query(string wql, string? wmiNamespace = null)
    {
        var scope = new ManagementScope(wmiNamespace ?? @"root\CIMV2");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using ManagementObjectCollection results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            if (item is ManagementObject managed)
            {
                yield return managed;
            }
        }
    }

    /// <summary>Lee una propiedad numérica sin importar si vino como uint, ulong o string.</summary>
    private static double? Number(ManagementBaseObject item, string property)
    {
        object? value = Read(item, property);

        return value switch
        {
            null => null,
            double d => d,
            float f => f,
            long l => l,
            ulong ul => ul,
            int i => i,
            uint ui => ui,
            short sh => sh,
            ushort ush => ush,
            byte b => b,
            sbyte sb => sb,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };
    }

    private static string? Text(ManagementBaseObject item, string property)
    {
        string? value = Read(item, property)?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool? Bool(ManagementBaseObject item, string property) =>
        Read(item, property) switch
        {
            bool b => b,
            string s when bool.TryParse(s, out bool parsed) => parsed,
            _ => null,
        };

    /// <summary>
    /// Una propiedad que no existe en esa versión de Windows tira <see cref="ManagementException"/>.
    /// Acá se trata como ausente, que es lo que es.
    /// </summary>
    private static object? Read(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static int? EventDataInt(EventRecord record, string fieldName)
    {
        // Se lee el XML y no las posiciones de Properties: los índices cambian entre versiones de
        // Windows, los nombres no.
        var xml = XElement.Parse(record.ToXml());
        XNamespace ns = xml.Name.Namespace;

        XElement? field = xml.Element(ns + "EventData")?
            .Elements(ns + "Data")
            .FirstOrDefault(d => string.Equals(
                d.Attribute("Name")?.Value, fieldName, StringComparison.OrdinalIgnoreCase));

        return int.TryParse(field?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static string? MemoryTypeName(double? smbiosType) => (int?)smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => null,
    };
}
