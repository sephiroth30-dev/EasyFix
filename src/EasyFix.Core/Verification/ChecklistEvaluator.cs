using System.Globalization;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Network;

namespace EasyFix.Core.Verification;

/// <summary>
/// Resultados del escaneo profundo (DISM, chkdsk), cuando corrió. Son opcionales porque el escaneo
/// profundo tarda minutos y vive detrás de «Reparar errores»; el checklist se puede evaluar sin él.
/// </summary>
/// <param name="SystemFilesHealthy"><c>null</c> = no se corrió DISM/SFC en esta sesión.</param>
/// <param name="FileSystemClean"><c>null</c> = no se corrió chkdsk en esta sesión.</param>
public sealed record DeepScanResults(bool? SystemFilesHealthy = null, bool? FileSystemClean = null)
{
    public static DeepScanResults NotRun { get; } = new();
}

/// <summary>
/// Evalúa la lista de condiciones del «estado estándar» sobre un snapshot. Lógica pura: se corre
/// antes y después de los arreglos y las dos pasadas se muestran en dos columnas.
/// </summary>
/// <remarks>
/// <para>Reglas de honestidad, heredadas de cuatro versiones de diagnósticos inventados:</para>
/// <list type="bullet">
/// <item>Un dato que no se midió produce <see cref="ChecklistStatus.NotDetermined"/>, nunca Pass.
/// Una comprobación que falló no es evidencia de que el equipo esté sano.</item>
/// <item>Cada estado lleva su motivo con el número medido adentro. Sin el número es una opinión.</item>
/// <item>Los datos de inestabilidad (pantallazos, errores de disco, dispositivos) tienen DOS modos de
/// fallo y los dos degradan a no determinado: la sonda <c>crash</c> entera caída (llega como
/// <see cref="CheckFailure"/>) y el fallo parcial por bloque — la sonda traga excepciones a propósito
/// y una lista vacía NO significa «cero problemas», por eso se consultan además los flags
/// <see cref="CrashData.CrashLogRead"/>, <see cref="CrashData.DiskEventsRead"/> y
/// <see cref="CrashData.DevicesRead"/>.</item>
/// </list>
/// </remarks>
public static class ChecklistEvaluator
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    public static ChecklistReport Evaluate(
        SystemSnapshot snapshot,
        CrashData crash,
        IReadOnlyList<CheckFailure> probeFailures,
        ThresholdOptions thresholds,
        DeepScanResults? deep = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(crash);
        ArgumentNullException.ThrowIfNull(probeFailures);
        ArgumentNullException.ThrowIfNull(thresholds);
        deep ??= DeepScanResults.NotRun;

        bool crashProbeFailed = probeFailures.Any(f => f.CheckId == "crash");

        var items = new List<ChecklistItem>
        {
            DiskHealth(snapshot),
            DiskSpace(snapshot, thresholds),
            MemoryPressure(snapshot, thresholds),
            SingleAntivirus(snapshot),
            NoProblemDevices(crash, crashProbeFailed),
            NoBlueScreens(crash, crashProbeFailed),
            NoDiskErrors(crash, crashProbeFailed),
            LightStartup(snapshot, thresholds),
            BootTime(snapshot, thresholds),
            Network(snapshot),
            SystemFiles(deep),
            FileSystem(deep),
        };

        return new ChecklistReport(items);
    }

    // ---- Condiciones --------------------------------------------------------------------------

    private static ChecklistItem DiskHealth(SystemSnapshot s) => s.PrimaryDiskHealth switch
    {
        Diagnostics.DiskHealth.Healthy => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.Pass,
            "El controlador reporta el disco sano."),

        Diagnostics.DiskHealth.Warning => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.Fail,
            "El controlador reporta advertencias. Respaldar datos y vigilar el disco.",
            NeedsHardware: true),

        Diagnostics.DiskHealth.Failing => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.Fail,
            "SMART predice falla del disco. Respaldar hoy y reemplazar.",
            NeedsHardware: true),

        _ when s.SmartPredictsFailure == true => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.Fail,
            "SMART predice falla del disco. Respaldar hoy y reemplazar.",
            NeedsHardware: true),

        _ when s.SmartPredictsFailure == false => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.NotDetermined,
            "SMART no predice falla, pero el estado general del disco no se pudo leer. No alcanza " +
            "para declararlo sano."),

        _ => new(
            "disk.health", "Salud del disco (SMART)", ChecklistStatus.NotDetermined,
            "La consulta de salud del disco no se completó."),
    };

    private static ChecklistItem DiskSpace(SystemSnapshot s, ThresholdOptions t)
    {
        if (s.SystemDriveFreePercent is not double free)
        {
            return new("disk.space", "Espacio libre suficiente en C:", ChecklistStatus.NotDetermined,
                "No se pudo medir el espacio libre.");
        }

        string measured = $"{free.ToString("0.#", Es)} % libre (mínimo {t.LowDiskFreePercent} %).";

        return free >= t.LowDiskFreePercent
            ? new("disk.space", "Espacio libre suficiente en C:", ChecklistStatus.Pass, measured)
            : new("disk.space", "Espacio libre suficiente en C:", ChecklistStatus.Fail,
                measured + " Sin margen, Windows no puede actualizar y todo se vuelve más lento.");
    }

    private static ChecklistItem MemoryPressure(SystemSnapshot s, ThresholdOptions t)
    {
        if (s.CommitUsedPercent is not double commit)
        {
            return new("memory.pressure", "Memoria sin saturación", ChecklistStatus.NotDetermined,
                "No se pudo medir el uso de memoria.");
        }

        string measured = $"Memoria comprometida al {commit.ToString("0.#", Es)} % " +
                          $"(límite {t.CommitPressurePercent} %).";

        if (commit <= t.CommitPressurePercent)
        {
            return new("memory.pressure", "Memoria sin saturación", ChecklistStatus.Pass, measured);
        }

        bool ramShort = s.TotalRamGb is double ram && ram < t.MinRamGb;
        return new("memory.pressure", "Memoria sin saturación", ChecklistStatus.Fail,
            measured + " El equipo está usando el disco como memoria.",
            NeedsHardware: ramShort);
    }

    private static ChecklistItem SingleAntivirus(SystemSnapshot s)
    {
        if (s.ActiveAntivirusCount is not int count)
        {
            return new("antivirus.single", "Un solo antivirus activo", ChecklistStatus.NotDetermined,
                "No se pudo consultar el centro de seguridad.");
        }

        string names = s.ActiveAntivirusNames is { Count: > 0 } list
            ? $" ({string.Join(", ", list)})"
            : string.Empty;

        return count switch
        {
            1 => new("antivirus.single", "Un solo antivirus activo", ChecklistStatus.Pass,
                $"1 antivirus con protección en tiempo real{names}."),
            0 => new("antivirus.single", "Un solo antivirus activo", ChecklistStatus.Fail,
                "Ningún antivirus con protección en tiempo real activa. Activar Microsoft Defender."),
            _ => new("antivirus.single", "Un solo antivirus activo", ChecklistStatus.Fail,
                $"{count} antivirus activos{names}: se estorban entre sí y el equipo se arrastra. " +
                "Dejar uno solo."),
        };
    }

    private static ChecklistItem NoProblemDevices(CrashData crash, bool crashProbeFailed)
    {
        if (crashProbeFailed || !crash.DevicesRead)
        {
            return new("devices.ok", "Sin dispositivos con problema", ChecklistStatus.NotDetermined,
                "La enumeración de dispositivos no se completó.");
        }

        return crash.ProblemDeviceNames.Count == 0
            ? new("devices.ok", "Sin dispositivos con problema", ChecklistStatus.Pass,
                "Ningún dispositivo reporta error de driver.")
            : new("devices.ok", "Sin dispositivos con problema", ChecklistStatus.Fail,
                $"{crash.ProblemDeviceNames.Count} dispositivo(s) con problema: " +
                $"{string.Join(", ", crash.ProblemDeviceNames.Take(3))}" +
                (crash.ProblemDeviceNames.Count > 3 ? "…" : "."));
    }

    private static ChecklistItem NoBlueScreens(CrashData crash, bool crashProbeFailed)
    {
        string title = $"Sin pantallazos en {crash.WindowDays} días";

        if (crashProbeFailed || !crash.CrashLogRead)
        {
            return new("crashes.none", title, ChecklistStatus.NotDetermined,
                "La lectura del registro de pantallazos no se completó.");
        }

        return crash.HasCrashes
            ? new("crashes.none", title, ChecklistStatus.Fail,
                $"{crash.Crashes.Count} pantallazo(s) y {crash.MinidumpCount} volcado(s) en la ventana analizada.")
            : new("crashes.none", title, ChecklistStatus.Pass,
                "Sin pantallazos ni volcados en la ventana analizada.");
    }

    private static ChecklistItem NoDiskErrors(CrashData crash, bool crashProbeFailed)
    {
        if (crashProbeFailed || !crash.DiskEventsRead)
        {
            return new("disk.events", "Sin errores de disco en el registro de eventos",
                ChecklistStatus.NotDetermined, "La lectura del registro de eventos no se completó.");
        }

        return crash.DiskErrorCount == 0
            ? new("disk.events", "Sin errores de disco en el registro de eventos",
                ChecklistStatus.Pass, "Ningún evento de error de disco, NTFS o volumen.")
            : new("disk.events", "Sin errores de disco en el registro de eventos",
                ChecklistStatus.Fail,
                $"{crash.DiskErrorCount} evento(s) de error de disco en {crash.WindowDays} días. " +
                "Puede ser el disco, el cable o el sistema de archivos.");
    }

    private static ChecklistItem LightStartup(SystemSnapshot s, ThresholdOptions t)
    {
        if (s.StartupDegradationMs is int ms)
        {
            double seconds = ms / 1000.0;
            string measured = $"Los programas de inicio retrasan el arranque " +
                              $"{seconds.ToString("0.#", Es)} s (tope {t.StartupDelayWarnSeconds} s).";

            return seconds <= t.StartupDelayWarnSeconds
                ? new("startup.light", "Inicio de Windows liviano", ChecklistStatus.Pass, measured)
                : new("startup.light", "Inicio de Windows liviano", ChecklistStatus.Fail, measured);
        }

        if (s.EnabledStartupEntryCount is int count)
        {
            // Sin la medición de retraso, el conteo es evidencia más débil; el umbral es amplio.
            return count <= t.StartupHeavyCount
                ? new("startup.light", "Inicio de Windows liviano", ChecklistStatus.Pass,
                    $"{count} programa(s) de inicio; no se pudo medir el retraso, pero la cantidad es razonable.")
                : new("startup.light", "Inicio de Windows liviano", ChecklistStatus.Fail,
                    $"{count} programas de inicio (tope {t.StartupHeavyCount}). El retraso exacto no " +
                    "se pudo medir, pero la cantidad ya es un problema.");
        }

        return new("startup.light", "Inicio de Windows liviano", ChecklistStatus.NotDetermined,
            "No se pudieron leer los programas de inicio.");
    }

    private static ChecklistItem BootTime(SystemSnapshot s, ThresholdOptions t)
    {
        if (s.MainPathBootTimeMs is not int ms || ms <= 0)
        {
            return new("boot.time", "Arranque dentro de lo esperable", ChecklistStatus.NotDetermined,
                "Windows todavía no registró una medición de arranque.");
        }

        double seconds = ms / 1000.0;
        string measured = $"Último arranque: {seconds.ToString("0.#", Es)} s " +
                          $"(tope {t.SlowBootSeconds} s).";

        return seconds <= t.SlowBootSeconds
            ? new("boot.time", "Arranque dentro de lo esperable", ChecklistStatus.Pass, measured)
            : new("boot.time", "Arranque dentro de lo esperable", ChecklistStatus.Fail, measured,
                NeedsHardware: s.IsSsd == false);
    }

    private static ChecklistItem Network(SystemSnapshot s) => s.Network switch
    {
        null => new("network.online", "Internet funcionando", ChecklistStatus.NotDetermined,
            "La conexión no se comprobó."),

        ConnectivityStatus.Online => new("network.online", "Internet funcionando",
            ChecklistStatus.Pass, "El equipo llega a internet."),

        ConnectivityStatus status => new("network.online", "Internet funcionando",
            ChecklistStatus.Fail, status switch
            {
                ConnectivityStatus.NoAdapter => "Sin cable de red ni wifi asociado.",
                ConnectivityStatus.DnsFailed => "Conectado a la red local pero sin resolver nombres.",
                ConnectivityStatus.CaptivePortal => "La red exige iniciar sesión en el navegador.",
                ConnectivityStatus.Unreachable => "Hay red pero la conexión no sale a internet.",
                _ => "La comprobación de internet no se pudo completar.",
            }),
    };

    private static ChecklistItem SystemFiles(DeepScanResults deep) => deep.SystemFilesHealthy switch
    {
        true => new("system.files", "Integridad de archivos de sistema (DISM/SFC)",
            ChecklistStatus.Pass, "DISM y SFC no encontraron daño."),
        false => new("system.files", "Integridad de archivos de sistema (DISM/SFC)",
            ChecklistStatus.Fail, "DISM o SFC reportaron archivos dañados."),
        null => new("system.files", "Integridad de archivos de sistema (DISM/SFC)",
            ChecklistStatus.NotDetermined,
            "Se evalúa con «Reparar errores»: el escaneo tarda varios minutos."),
    };

    private static ChecklistItem FileSystem(DeepScanResults deep) => deep.FileSystemClean switch
    {
        true => new("filesystem.clean", "Sistema de archivos sin errores (chkdsk)",
            ChecklistStatus.Pass, "chkdsk no encontró errores."),
        false => new("filesystem.clean", "Sistema de archivos sin errores (chkdsk)",
            ChecklistStatus.Fail, "chkdsk reportó errores en el sistema de archivos."),
        null => new("filesystem.clean", "Sistema de archivos sin errores (chkdsk)",
            ChecklistStatus.NotDetermined,
            "Se evalúa con «Reparar errores»: el escaneo tarda varios minutos."),
    };
}
