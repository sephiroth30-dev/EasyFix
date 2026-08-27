using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using EasyFix.Core.Configuration;
using EasyFix.Core.Fixes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Rollback;

/// <summary>
/// Crea puntos de restauración y verifica que hayan quedado creados.
/// </summary>
/// <remarks>
/// <para>Ver <see cref="RestorePointPolicy"/> para el porqué de la verificación con espera: la API es
/// asíncrona y la versión anterior daba falso negativo siempre.</para>
///
/// <para><b>El límite de 24 h.</b> Windows no crea más de un punto por día salvo que se cambie
/// <c>SystemRestorePointCreationFrequency</c>. En el equipo de un cliente que ya tuvo actividad ese
/// día, eso hace que la herramienta no pueda crear su red de seguridad — que es exactamente lo que
/// pasó en la primera prueba real. Se neutraliza, con tres condiciones: se informa el valor anterior
/// para poder revertirlo, es configurable, y queda en el journal.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RestorePointService : IRestorePointService
{
    private const string Namespace = @"root\default";
    private const string ClassName = "SystemRestore";

    private const string ThrottleKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string ThrottleValueName = "SystemRestorePointCreationFrequency";

    /// <summary>MODIFY_SETTINGS: el tipo correcto para cambios de configuración.</summary>
    private const uint RestorePointTypeModifySettings = 12;

    /// <summary>BEGIN_SYSTEM_CHANGE.</summary>
    private const uint EventTypeBeginSystemChange = 100;

    private readonly ThresholdOptions _thresholds;
    private readonly TimeProvider _time;
    private readonly ILogger<RestorePointService> _logger;

    public RestorePointService(
        ThresholdOptions thresholds,
        TimeProvider time,
        ILogger<RestorePointService> logger)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _thresholds = thresholds;
        _time = time;
        _logger = logger;
    }

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        // La lectura previa es una señal de respaldo, no un requisito: si falla, queda en null y la
        // verificación se apoya en la fecha del punto más nuevo.
        long? sequenceBefore = await Task.Run(ReadHighestSequence, ct).ConfigureAwait(false);

        bool throttleDisabled = false;
        int? previousThrottle = null;

        if (_thresholds.DisableRestorePointThrottle)
        {
            (throttleDisabled, previousThrottle) = await Task
                .Run(TryDisableThrottle, ct)
                .ConfigureAwait(false);
        }

        if (!await Task.Run(() => TryCreate(description), ct).ConfigureAwait(false))
        {
            // Puede ser que Restaurar sistema esté deshabilitado. Se intenta habilitarlo una vez:
            // dejarlo habilitado es una mejora en sí misma, un equipo sin puntos no tiene vuelta atrás.
            _logger.LogWarning("CreateRestorePoint falló. Se intenta habilitar Restaurar sistema.");

            if (!await Task.Run(TryEnable, ct).ConfigureAwait(false) ||
                !await Task.Run(() => TryCreate(description), ct).ConfigureAwait(false))
            {
                return new RestorePointResult(
                    null, throttleDisabled, previousThrottle,
                    "No se pudo crear el punto de restauración. Restaurar sistema puede estar " +
                    "deshabilitado por directiva, o no haber espacio libre en C:.");
            }
        }

        // ---- Esperar a que aparezca ---------------------------------------------------------
        DateTimeOffset deadline = _time.GetUtcNow() + RestorePointPolicy.PollTimeout;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            RestorePointInfo? newest = await Task.Run(ReadNewest, ct).ConfigureAwait(false);
            bool attemptsRemain = _time.GetUtcNow() < deadline;

            RestorePointCheck verdict = RestorePointPolicy.Evaluate(
                newest, sequenceBefore, _time.GetUtcNow(), attemptsRemain);

            if (verdict == RestorePointCheck.Created)
            {
                _logger.LogInformation(
                    "Punto de restauración verificado: secuencia {Sequence}, creado {Created}, " +
                    "tras {Attempts} intento(s).",
                    newest!.Sequence, newest.CreatedUtc, attempt + 1);

                return new RestorePointResult(newest.Sequence, throttleDisabled, previousThrottle);
            }

            if (verdict == RestorePointCheck.CannotVerify)
            {
                _logger.LogWarning(
                    "No se pudo verificar el punto de restauración tras {Seconds} s y {Attempts} " +
                    "intento(s). Secuencia previa: {Before}. Más nuevo leído: {Newest}.",
                    RestorePointPolicy.PollTimeout.TotalSeconds, attempt + 1,
                    sequenceBefore, newest?.Sequence);

                return new RestorePointResult(
                    null, throttleDisabled, previousThrottle,
                    $"Se pidió el punto de restauración pero no apareció en " +
                    $"{RestorePointPolicy.PollTimeout.TotalSeconds:0} segundos. Puede que el servicio " +
                    "de instantáneas de volumen (VSS) esté detenido o sin espacio.");
            }

            attempt++;
            await Task.Delay(RestorePointPolicy.PollInterval, ct).ConfigureAwait(false);
        }
    }

    // ---- Límite de frecuencia ---------------------------------------------------------------

    /// <summary>
    /// Pone el límite de creación en 0. Devuelve si lo cambió y cuál era el valor anterior.
    /// </summary>
    private (bool Changed, int? Previous) TryDisableThrottle()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ThrottleKeyPath, writable: true)
                                    ?? Registry.LocalMachine.CreateSubKey(ThrottleKeyPath);

            if (key is null)
            {
                _logger.LogWarning("No se pudo abrir {Key} para quitar el límite de frecuencia.", ThrottleKeyPath);
                return (false, null);
            }

            object? current = key.GetValue(ThrottleValueName);
            int? previous = current is int value ? value : null;

            // Ya está en 0: no hay nada que cambiar ni que revertir después.
            if (previous == 0)
            {
                return (false, 0);
            }

            key.SetValue(ThrottleValueName, 0, RegistryValueKind.DWord);

            _logger.LogInformation(
                "Límite de frecuencia de puntos de restauración puesto en 0 (antes: {Previous}). " +
                "Se revierte con «Deshacer todo».",
                previous?.ToString(CultureInfo.InvariantCulture) ?? "sin definir");

            return (true, previous);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Sin permisos para quitar el límite de frecuencia.");
            return (false, null);
        }
    }

    // ---- WMI --------------------------------------------------------------------------------

    private bool TryCreate(string description)
    {
        try
        {
            using var systemRestore = new ManagementClass(
                new ManagementScope(Namespace), new ManagementPath(ClassName), null);

            using ManagementBaseObject parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = RestorePointTypeModifySettings;
            parameters["EventType"] = EventTypeBeginSystemChange;

            using ManagementBaseObject result =
                systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);

            uint code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);

            if (code != 0)
            {
                _logger.LogWarning("CreateRestorePoint devolvió {Code}.", code);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Falló la llamada a CreateRestorePoint.");
            return false;
        }
    }

    private bool TryEnable()
    {
        try
        {
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

            using var systemRestore = new ManagementClass(
                new ManagementScope(Namespace), new ManagementPath(ClassName), null);

            using ManagementBaseObject parameters = systemRestore.GetMethodParameters("Enable");
            parameters["Drive"] = systemDrive;
            parameters["WaitTillEnabled"] = true;

            using ManagementBaseObject result = systemRestore.InvokeMethod("Enable", parameters, null);

            uint code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);
            if (code == 0)
            {
                _logger.LogInformation("Restaurar sistema habilitado en {Drive}.", systemDrive);
                return true;
            }

            _logger.LogWarning("Enable devolvió {Code}.", code);
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Falló la llamada a Enable.");
            return false;
        }
    }

    /// <summary>
    /// Punto más nuevo por secuencia. <c>null</c> si la consulta falló.
    /// </summary>
    /// <remarks>
    /// <b>Distinguir "no hay" de "no pude leer" es el punto.</b> La versión anterior devolvía 0 en
    /// los dos casos, y 0 se interpretaba como "no hay ninguno". En la prueba real una consulta falló
    /// y veinte segundos después devolvía 213: un fallo de lectura se estaba tratando como un hecho.
    /// </remarks>
    private RestorePointInfo? ReadNewest()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Namespace),
                new ObjectQuery($"SELECT SequenceNumber, CreationTime, Description FROM {ClassName}"));

            RestorePointInfo? newest = null;
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    try
                    {
                        long sequence = Convert.ToInt64(item["SequenceNumber"], CultureInfo.InvariantCulture);

                        if (newest is null || sequence > newest.Sequence)
                        {
                            newest = new RestorePointInfo(
                                sequence,
                                RestorePointPolicy.ParseWmiDate(item["CreationTime"]?.ToString()),
                                item["Description"]?.ToString());
                        }
                    }
                    catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
                    {
                        // Un punto ilegible no invalida los demás.
                    }
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudieron enumerar los puntos de restauración.");
            return null;   // null = no pude leer. NO es lo mismo que "no hay".
        }
    }

    private long? ReadHighestSequence() => ReadNewest()?.Sequence;
}

/// <summary>
/// Suspende BitLocker antes de los fixes que tocan arranque o disco.
/// </summary>
/// <remarks>
/// <c>DisableKeyProtectors(1)</c> suspende la protección y la reactiva sola en el reinicio siguiente.
/// Es lo que evita que Windows pida la clave de recuperación de 48 dígitos y deje al cliente fuera de
/// su propio equipo.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BitLockerService : IBitLockerService
{
    private const string Namespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    private readonly ILogger<BitLockerService> _logger;

    public BitLockerService(ILogger<BitLockerService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<bool> SuspendUntilNextRebootAsync(CancellationToken ct) =>
        await Task.Run(Suspend, ct).ConfigureAwait(false);

    private bool Suspend()
    {
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Namespace),
                new ObjectQuery(
                    "SELECT * FROM Win32_EncryptableVolume " +
                    $"WHERE DriveLetter = '{systemDrive}'"));

            using ManagementObjectCollection volumes = searcher.Get();

            foreach (ManagementBaseObject item in volumes)
            {
                if (item is not ManagementObject volume)
                {
                    continue;
                }

                using (volume)
                {
                    using ManagementBaseObject parameters =
                        volume.GetMethodParameters("DisableKeyProtectors");

                    // 1 = se reactiva sola en el próximo reinicio. Dejar BitLocker suspendido de
                    // forma indefinida sería peor que el problema que estamos evitando.
                    parameters["DisableCount"] = (uint)1;

                    using ManagementBaseObject result =
                        volume.InvokeMethod("DisableKeyProtectors", parameters, null);

                    uint code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);

                    if (code == 0)
                    {
                        _logger.LogInformation(
                            "BitLocker suspendido en {Drive} hasta el próximo reinicio.", systemDrive);
                        return true;
                    }

                    _logger.LogWarning("DisableKeyProtectors devolvió 0x{Code:X8}.", code);
                    return false;
                }
            }

            _logger.LogWarning("No se encontró el volumen {Drive} en el namespace de BitLocker.", systemDrive);
            return false;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Falló la suspensión de BitLocker.");
            return false;
        }
    }
}
