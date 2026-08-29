using EasyFix.Core.Abstractions;
using EasyFix.Core.Configuration;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Processes;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Fixes;

/// <summary>Base de los fixes que corren un binario de Windows.</summary>
/// <remarks>
/// Concentra el patrón que comparten: resolver la ruta absoluta en <c>System32</c>, correr con
/// timeout, y registrar en el journal antes de actuar. Ver <see cref="SafeProcessRunner"/> para las
/// defensas de invocación.
/// </remarks>
public abstract class ProcessFix : IFix
{
    protected ProcessFix(IProcessRunner runner, ThresholdOptions thresholds, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(logger);

        Runner = runner;
        Thresholds = thresholds;
        Log = logger;
    }

    protected IProcessRunner Runner { get; }
    protected ThresholdOptions Thresholds { get; }
    protected ILogger Log { get; }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract FixTier Tier { get; }

    /// <summary>Los fixes basados en procesos de Windows son todos de reparación.</summary>
    public virtual FixCategory Category => FixCategory.Repair;

    public virtual bool RequiresReboot => false;
    public virtual bool TouchesBootOrDisk => false;
    public virtual bool IsReversible => false;
    public virtual bool IsLongRunning => false;

    public abstract Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct);

    public abstract Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct);

    /// <summary>Corre un binario de <c>System32</c> con el timeout configurado.</summary>
    protected Task<ProcessResult> RunSystem32Async(
        string executableName,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        IReadOnlyCollection<int>? benignExitCodes = null) =>
        Runner.RunAsync(
            SafeProcessRunner.System32(executableName),
            arguments,
            Thresholds.ExternalProcessTimeout,
            ct,
            benignExitCodes);

    /// <summary>Primera línea no vacía de un texto, para mensajes de error.</summary>
    protected static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>Registra la acción antes de aplicarla. Nunca después.</summary>
    protected void Journal(FixContext context, string target, string? note = null, long? freedBytes = null) =>
        context.Journal.Append(new JournalAction
        {
            FixId = Id,
            Target = target,
            Reversible = IsReversible,
            FreedBytes = freedBytes,
            Note = note,
        });
}

/// <summary>
/// Restaura los archivos del sistema contra la imagen de referencia de Windows.
/// </summary>
/// <remarks>
/// <para>Es el fix que <b>realmente</b> devuelve el equipo a estado estándar: DISM compara la
/// instalación contra la imagen de componentes y repone lo que esté modificado o corrupto. Después
/// <c>sfc</c> hace lo mismo a nivel de archivos protegidos.</para>
///
/// <para>Primero <c>ScanHealth</c>, que es de solo lectura, y solo si encuentra daño se corre
/// <c>RestoreHealth</c>: escanear tarda minutos, repararlo tarda mucho más, y no tiene sentido pagar
/// ese costo en un equipo sano.</para>
///
/// <para><b>Nunca <c>/ResetBase</c>.</b> Deja el equipo sin poder desinstalar actualizaciones, que es
/// justo lo que puede hacer falta cuando una actualización es la causa del problema.</para>
/// </remarks>
public sealed class SystemFileRepairFix : ProcessFix
{
    public SystemFileRepairFix(
        IProcessRunner runner, ThresholdOptions thresholds, ILogger<SystemFileRepairFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "system.repair-files";
    public override string DisplayName => "Reparar los archivos del sistema";

    public override string Description =>
        "Compara Windows contra su imagen de referencia y repone los archivos modificados o " +
        "corruptos. Es lo que devuelve el sistema a estado estándar. Tarda entre 10 y 40 minutos.";

    public override FixTier Tier => FixTier.SafeAuto;
    public override bool IsLongRunning => true;

    // No toca el arranque ni la tabla de particiones: repone archivos. No dispara BitLocker.
    public override bool TouchesBootOrDisk => false;

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct) =>
        Task.FromResult(FixApplicability.Yes(
            "Siempre corresponde: si no hay daño, el escaneo lo confirma y no cambia nada."));

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        log.Report("Revisando si Windows tiene archivos dañados. Esto tarda varios minutos.");

        // /English fuerza salida en inglés sin importar el idioma del sistema. Sin esto la detección
        // de "no hay corrupción" compara texto en inglés contra un Windows en español, nunca acierta,
        // y RestoreHealth corre siempre: ~4 minutos perdidos en cada corrida.
        ProcessResult scan = await RunSystem32Async(
            "Dism.exe",
            new[] { "/Online", "/Cleanup-Image", "/ScanHealth", "/English" },
            ct).ConfigureAwait(false);

        if (scan.TimedOut)
        {
            return FixOutcome.Failed(
                "El escaneo de integridad excedió el tiempo límite. Suele indicar un disco muy lento " +
                "o con errores.");
        }

        // Con /English la salida es determinista: "No component store corruption detected." cuando
        // está sano, y "The component store is repairable." cuando hay daño reparable.
        bool repairable = Mentions(scan, "component store is repairable");
        bool noCorruption = Mentions(scan, "No component store corruption detected");

        if (noCorruption && !repairable)
        {
            log.Report("Los archivos de Windows están bien.");
            return FixOutcome.NothingToDo("Los archivos del sistema ya estaban íntegros: no hizo falta reparar.");
        }

        // Hay daño (o no se pudo determinar): se repara. En la duda conviene repararlo.
        log.Report("Se encontraron archivos dañados. Reponiéndolos desde Windows. Puede tardar media hora.");

        Journal(context, "Imagen de componentes de Windows",
            "DISM /RestoreHealth. No es reversible, pero solo repone archivos originales de Windows.");

        ProcessResult restore = await RunSystem32Async(
            "Dism.exe",
            new[] { "/Online", "/Cleanup-Image", "/RestoreHealth", "/English" },
            ct).ConfigureAwait(false);

        if (restore.TimedOut)
        {
            return FixOutcome.Failed(
                "La reparación excedió el tiempo límite. Si el equipo no tiene internet, DISM no puede " +
                "descargar los archivos de reemplazo.");
        }

        if (!restore.Succeeded)
        {
            // Es la señal más fuerte de que hace falta el reset propio de Windows.
            return FixOutcome.Failed(
                $"DISM no pudo reparar la imagen (código {restore.ExitCode}). Cuando esto falla, lo " +
                "que corresponde es «Restablecer este PC conservando mis archivos»: el daño excede lo " +
                "que se puede reparar pieza por pieza.");
        }

        log.Report("Verificando los archivos protegidos de Windows.");

        Journal(context, "Archivos protegidos del sistema", "sfc /scannow.");

        ProcessResult sfc = await RunSystem32Async("sfc.exe", new[] { "/scannow" }, ct)
            .ConfigureAwait(false);

        if (sfc.TimedOut)
        {
            return FixOutcome.NeedsReboot(
                "La imagen se reparó, pero la verificación de archivos protegidos no terminó a tiempo. " +
                "Conviene reiniciar y repetir.");
        }

        // sfc no tiene equivalente de /English, así que acá sí hay que buscar las dos variantes.
        bool needsReboot = Mentions(sfc, "restart") || Mentions(sfc, "reiniciar");

        string summary = needsReboot
            ? "Se repararon archivos del sistema. Hace falta reiniciar para completarlo."
            : "Se repararon los archivos del sistema y se verificaron los protegidos.";

        return needsReboot ? FixOutcome.NeedsReboot(summary) : FixOutcome.Applied(summary);
    }

    /// <summary>
    /// DISM y sfc responden en el idioma del sistema, así que se buscan las dos variantes.
    /// </summary>
    private static bool Mentions(ProcessResult result, string fragment) =>
        result.StandardOutput.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Revisa el sistema de archivos y, si hay errores, programa la reparación para el próximo reinicio.
/// </summary>
/// <remarks>
/// Primero <c>chkdsk /scan</c>, que corre en línea y es de solo lectura. Solo si encuentra errores se
/// programa <c>chkdsk /f</c>, que necesita el volumen desmontado y por lo tanto un reinicio.
/// <para><b>Toca el disco</b>, así que pasa por la compuerta de BitLocker.</para>
/// </remarks>
public sealed class DiskCheckFix : ProcessFix
{
    public DiskCheckFix(IProcessRunner runner, ThresholdOptions thresholds, ILogger<DiskCheckFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "disk.chkdsk";
    public override string DisplayName => "Revisar el sistema de archivos";

    public override string Description =>
        "Busca errores en el sistema de archivos. Si encuentra, programa la reparación para el " +
        "próximo reinicio. Un sistema de archivos con errores causa pantallazos y corrompe programas.";

    public override FixTier Tier => FixTier.SafeAuto;
    public override bool IsLongRunning => true;
    public override bool TouchesBootOrDisk => true;

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        // En un disco que ya se sabe fallando, el FixRunner aborta antes de llegar acá. Esto cubre el
        // caso de "advertencia": chkdsk sobre un disco moribundo puede ser el empujón final.
        if (context.Snapshot.PrimaryDiskHealth == DiskHealth.Warning)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.DiskFailing,
                "El disco reporta advertencias de salud. Revisarlo a fondo puede acelerar la falla: " +
                "primero respaldá los datos."));
        }

        return Task.FromResult(FixApplicability.Yes());
    }

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        log.Report($"Revisando el disco {drive} en busca de errores. No hace falta reiniciar.");

        ProcessResult scan = await RunSystem32Async("chkdsk.exe", new[] { drive, "/scan" }, ct)
            .ConfigureAwait(false);

        if (scan.TimedOut)
        {
            return FixOutcome.Failed(
                "La revisión del disco excedió el tiempo límite. En un disco con errores de lectura " +
                "esto es esperable, y es en sí mismo una señal de que el disco está fallando.");
        }

        // chkdsk /scan devuelve 0 cuando no hay errores; distinto de 0 cuando encontró algo.
        if (scan.Succeeded)
        {
            return FixOutcome.NothingToDo("El sistema de archivos no tiene errores.");
        }

        log.Report("El disco tiene errores. La reparación queda programada para el próximo reinicio.");

        Journal(context, drive,
            $"chkdsk {drive} /f programado en el próximo reinicio. La reparación del sistema de " +
            "archivos no es reversible.");

        // /f necesita el volumen desmontado: se programa. La 'S' responde "sí" al pedido de
        // programarlo, y va por ArgumentList igual que todo lo demás.
        ProcessResult schedule = await RunSystem32Async(
            "chkdsk.exe", new[] { drive, "/f", "/spotfix" }, ct).ConfigureAwait(false);

        return schedule.TimedOut || schedule.ExitCode is null
            ? FixOutcome.Failed("No se pudo programar la reparación del disco.")
            : FixOutcome.NeedsReboot(
                $"Se encontraron errores en {drive} y la reparación quedó programada. " +
                "Hace falta reiniciar para que corra.");
    }
}

/// <summary>
/// Programa el diagnóstico de memoria de Windows para el próximo reinicio.
/// </summary>
/// <remarks>
/// No arregla nada: <b>mide</b>. Es la única forma de confirmar o descartar la RAM, que es el primer
/// sospechoso de varios códigos de parada. Si la memoria está mal, hay que reemplazarla.
/// <para>Requiere aprobación porque el diagnóstico se apropia del arranque siguiente y puede tardar
/// horas: no es algo que deba pasarle por sorpresa al cliente.</para>
/// </remarks>
public sealed class ScheduleMemoryTestFix : ProcessFix
{
    public ScheduleMemoryTestFix(
        IProcessRunner runner, ThresholdOptions thresholds, ILogger<ScheduleMemoryTestFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "memory.schedule-test";
    public override string DisplayName => "Programar el diagnóstico de memoria";

    public override string Description =>
        "Programa la prueba de memoria de Windows para el próximo reinicio. No repara: confirma o " +
        "descarta que la RAM esté fallando. Puede tardar de 20 minutos a varias horas, y el equipo " +
        "no se puede usar mientras corre.";

    public override FixTier Tier => FixTier.RequiresApproval;
    public override bool RequiresReboot => true;
    public override bool TouchesBootOrDisk => true;   // escribe en la configuración de arranque

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        // Solo se ofrece cuando hay evidencia que lo justifique.
        bool memorySuspect = context.Crash?.DominantSuspect == CrashSuspect.Memory;
        bool memoryPressure = context.Snapshot.CommitUsedPercent > 95;

        if (!memorySuspect && !memoryPressure)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.NotNeeded,
                "Nada apunta a la memoria: no hace falta ocupar el próximo arranque con la prueba."));
        }

        return Task.FromResult(FixApplicability.Yes(
            memorySuspect
                ? "El código de parada de los pantallazos apunta a la memoria."
                : "La memoria está al límite de su capacidad comprometida."));
    }

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        log.Report("La prueba de memoria queda programada para el próximo reinicio.");

        Journal(context, "Configuración de arranque",
            "Diagnóstico de memoria de Windows programado. Se ejecuta una sola vez.");

        // bcdedit marca el diagnóstico para el próximo arranque. mdsched.exe reinicia de inmediato,
        // que no es lo que queremos: el técnico decide cuándo.
        ProcessResult result = await RunSystem32Async(
            "bcdedit.exe", new[] { "/bootsequence", "{memdiag}", "/addfirst" }, ct)
            .ConfigureAwait(false);

        return result.Succeeded
            ? FixOutcome.NeedsReboot(
                "El diagnóstico de memoria corre en el próximo reinicio. El resultado queda en el " +
                "registro de eventos, en «MemoryDiagnostics-Results».")
            : FixOutcome.Failed(
                $"No se pudo programar el diagnóstico de memoria (código {result.ExitCode}). " +
                "Se puede lanzar a mano con mdsched.exe.");
    }
}

/// <summary>
/// Devuelve la pila de red a sus valores predeterminados.
/// </summary>
/// <remarks>
/// Es reparación de deriva de configuración pura: winsock e IP vuelven a como venían de fábrica.
/// Arregla los pantallazos causados por drivers de red mal configurados y los problemas de conexión
/// que no se explican de otra forma. Necesita reinicio y <b>toca la configuración de red</b>, así que
/// pasa por la compuerta de BitLocker.
/// </remarks>
public sealed class NetworkStackResetFix : ProcessFix
{
    public NetworkStackResetFix(
        IProcessRunner runner, ThresholdOptions thresholds, ILogger<NetworkStackResetFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "network.reset";
    public override string DisplayName => "Restablecer la red a valores predeterminados";

    public override string Description =>
        "Devuelve winsock y la configuración IP a como vienen de fábrica. Requiere reiniciar. " +
        "Si el equipo tiene una VPN o una configuración de red manual, hay que rehacerla.";

    public override FixTier Tier => FixTier.RequiresApproval;
    public override bool RequiresReboot => true;
    public override bool TouchesBootOrDisk => true;

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        if (context.Snapshot.IsDomainJoined)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.DomainManaged,
                "En un equipo de dominio esto rompe la conexión con los recursos de la empresa."));
        }

        return Task.FromResult(FixApplicability.Yes());
    }

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        Journal(context, "Pila de red (winsock e IP)",
            "netsh winsock reset + netsh int ip reset. La configuración manual de red se pierde.");

        log.Report("Restableciendo la configuración de red.");
        ProcessResult winsock = await RunSystem32Async(
            "netsh.exe", new[] { "winsock", "reset" }, ct).ConfigureAwait(false);

        log.Report("Restableciendo las direcciones IP.");

        // 1 es habitual acá: netsh no puede reponer algunas claves que ya estaban en su valor por
        // defecto, y devuelve 1 aunque el restablecimiento haya funcionado.
        ProcessResult ip = await RunSystem32Async(
            "netsh.exe", new[] { "int", "ip", "reset" }, ct, new[] { 1 }).ConfigureAwait(false);

        log.Report("Limpiando la memoria de nombres de red.");
        await RunSystem32Async("ipconfig.exe", new[] { "/flushdns" }, ct).ConfigureAwait(false);

        // "netsh int ip reset" devuelve 1 con frecuencia aunque haya funcionado: no puede reponer
        // algunas claves que ya estaban en su valor por defecto. Solo se considera fallo si winsock
        // —que sí es fiable— también falló.
        if (!winsock.Succeeded)
        {
            return FixOutcome.Failed(
                $"No se pudo restablecer winsock (código {winsock.ExitCode}). " +
                FirstLine(winsock.StandardError));
        }

        if (!ip.Succeeded)
        {
            Log.LogInformation(
                "«netsh int ip reset» devolvió {Code}; es habitual y no impide el restablecimiento.",
                ip.ExitCode);
        }

        return FixOutcome.NeedsReboot(
            "La pila de red volvió a sus valores predeterminados. Hace falta reiniciar.");
    }
}

/// <summary>
/// Desinstala la actualización de Windows que coincide en fecha con los pantallazos.
/// </summary>
/// <remarks>
/// <para>Es el fix que atiende directamente la hipótesis «fue una actualización». Requiere aprobación
/// y no se ofrece solo: desinstalar una actualización deja el equipo sin sus parches de seguridad, y
/// Windows Update la va a reinstalar salvo que se la pause.</para>
///
/// <para><b>No se ofrece cuando hay evidencia de hardware.</b> En ese caso la actualización solo
/// destapó un problema que ya existía, y desinstalarla calla el síntoma sin resolver la causa —
/// dejando además el equipo sin parchear.</para>
/// </remarks>
public sealed class RemoveCorrelatedUpdateFix : ProcessFix
{
    public RemoveCorrelatedUpdateFix(
        IProcessRunner runner, ThresholdOptions thresholds, ILogger<RemoveCorrelatedUpdateFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "update.remove";
    public override string DisplayName => "Desinstalar la actualización sospechosa";

    public override string Description =>
        "Desinstala la actualización de Windows instalada justo antes de que empezaran los " +
        "pantallazos. El equipo queda sin ese parche de seguridad, y Windows Update lo va a " +
        "reinstalar si no se pausa.";

    public override FixTier Tier => FixTier.RequiresApproval;
    public override bool RequiresReboot => true;
    public override bool IsReversible => true;   // se puede reinstalar desde Windows Update

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        if (context.Crash is not { } crash || crash.CorrelatedUpdates.Count == 0)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.NotNeeded,
                "Ningún pantallazo coincide con una actualización reciente, así que no hay nada que " +
                "desinstalar."));
        }

        if (crash.HardwareConfirmed)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.Undetermined,
                "Hay evidencia de falla de hardware. La actualización probablemente solo destapó un " +
                "problema que ya existía: desinstalarla callaría el síntoma y dejaría el equipo sin " +
                "parches de seguridad."));
        }

        return Task.FromResult(FixApplicability.Yes(
            $"Coincide en fecha con {crash.CorrelatedUpdates.Count} actualización(es)."));
    }

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        InstalledUpdate target = context.Crash!.CorrelatedUpdates[0];

        // El identificador viene como "KB5034441": se extrae el número, que es lo que espera wusa.
        string number = new string(target.Id.Where(char.IsDigit).ToArray());
        if (number.Length == 0)
        {
            return FixOutcome.Failed(
                $"No se pudo extraer el número de KB de '{target.Id}'.");
        }

        log.Report($"Desinstalando la actualización {target.Id}.");

        Journal(context, target.Id,
            $"Desinstalada la actualización {target.Id}, instalada el " +
            $"{target.InstalledOn:yyyy-MM-dd}. Se puede reinstalar desde Windows Update.");

        ProcessResult result = await RunSystem32Async(
            "wusa.exe", new[] { $"/uninstall", $"/kb:{number}", "/quiet", "/norestart" }, ct)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            return FixOutcome.Failed(
                $"La desinstalación de {target.Id} excedió el tiempo límite.");
        }

        if (!result.Succeeded)
        {
            return FixOutcome.Failed(
                $"No se pudo desinstalar {target.Id} (código {result.ExitCode}). Las actualizaciones " +
                "acumulativas modernas a veces no se pueden quitar con wusa: probá desde " +
                "Configuración → Windows Update → Historial → Desinstalar actualizaciones.");
        }

        return FixOutcome.NeedsReboot(
            $"Se desinstaló {target.Id}. Hace falta reiniciar. Conviene pausar Windows Update unos " +
            "días para confirmar si los pantallazos se detienen.");
    }
}

/// <summary>
/// Restablece los componentes de Windows Update.
/// </summary>
/// <remarks>
/// Detiene los servicios, renombra las carpetas de estado y los vuelve a arrancar: Windows las
/// reconstruye. Arregla las actualizaciones que fallan en bucle, que a su vez pueden ser la causa de
/// pantallazos si dejan una instalación a medias.
/// </remarks>
public sealed class WindowsUpdateResetFix : ProcessFix
{
    private static readonly string[] Services = { "wuauserv", "bits", "cryptsvc" };

    /// <summary>net.exe devuelve 2 cuando el servicio ya estaba en el estado pedido.</summary>
    private static readonly int[] ServiceAlreadyInState = { 2 };

    public WindowsUpdateResetFix(
        IProcessRunner runner, ThresholdOptions thresholds, ILogger<WindowsUpdateResetFix> logger)
        : base(runner, thresholds, logger) { }

    public override string Id => "update.reset-components";
    public override string DisplayName => "Restablecer Windows Update";

    public override string Description =>
        "Reconstruye el estado de Windows Update. Arregla las actualizaciones que fallan una y otra " +
        "vez. La próxima búsqueda de actualizaciones va a tardar más de lo normal.";

    public override FixTier Tier => FixTier.SafeAuto;
    public override bool IsLongRunning => true;

    public override Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct) =>
        Task.FromResult(FixApplicability.Yes());

    public override async Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct)
    {
        Journal(context, "Componentes de Windows Update",
            "Servicios detenidos y carpetas de estado renombradas. Windows las reconstruye.");

        foreach (string service in Services)
        {
            log.Report($"Deteniendo el servicio {service}.");

            // 2 = el servicio ya estaba detenido. Es el caso normal.
            ProcessResult stop = await RunSystem32Async(
                    "net.exe", new[] { "stop", service }, ct, ServiceAlreadyInState)
                .ConfigureAwait(false);

            // net.exe devuelve 2 cuando el servicio YA estaba detenido. Es el caso normal, no un
            // fallo: en la primera prueba real BITS estaba parado y ensuciaba el log con una
            // advertencia por corrida.
            if (!stop.Succeeded && stop.ExitCode != 2)
            {
                Log.LogWarning(
                    "No se pudo detener {Service} (código {Code}).", service, stop.ExitCode);
            }
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var renamed = new List<string>();

        foreach (string folder in new[] { "SoftwareDistribution", "System32\\catroot2" })
        {
            ct.ThrowIfCancellationRequested();

            string path = Path.Combine(root, folder);
            string backup = path + ".easyfix-old";

            try
            {
                if (Directory.Exists(path))
                {
                    if (Directory.Exists(backup))
                    {
                        Directory.Delete(backup, recursive: true);
                    }

                    Directory.Move(path, backup);
                    renamed.Add(folder);
                    log.Report($"Estado de Windows Update reconstruido ({folder}).");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Un archivo en uso impide el renombrado. Se sigue: el otro puede alcanzar.
                Log.LogWarning(ex, "No se pudo renombrar {Folder}.", folder);
            }
        }

        foreach (string service in Services)
        {
            log.Report($"Volviendo a arrancar {service}.");

            ProcessResult start = await RunSystem32Async(
                    "net.exe", new[] { "start", service }, ct, ServiceAlreadyInState)
                .ConfigureAwait(false);

            // 2 = ya estaba corriendo. Tampoco es un fallo.
            if (!start.Succeeded && start.ExitCode != 2)
            {
                Log.LogWarning(
                    "No se pudo arrancar {Service} (código {Code}). Windows lo arranca solo cuando " +
                    "haga falta.", service, start.ExitCode);
            }
        }

        return renamed.Count == 0
            ? FixOutcome.NothingToDo(
                "No se pudo renombrar ninguna carpeta de estado (archivos en uso). Conviene reintentar " +
                "después de reiniciar.")
            : FixOutcome.Applied(
                $"Windows Update restablecido: se reconstruyeron {string.Join(" y ", renamed)}.");
    }
}
