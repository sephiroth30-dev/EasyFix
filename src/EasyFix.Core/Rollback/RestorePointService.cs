using System.Management;
using System.Runtime.Versioning;
using EasyFix.Core.Fixes;
using Microsoft.Extensions.Logging;

namespace EasyFix.Core.Rollback;

/// <summary>
/// Crea puntos de restauración con WMI, y <b>verifica que hayan quedado creados</b>.
/// </summary>
/// <remarks>
/// <para><b>Por qué hay que verificar y no alcanza con llamar.</b> Windows aplica un límite de
/// frecuencia: si ya se creó un punto en las últimas 24 horas,
/// <c>SystemRestore.CreateRestorePoint</c> devuelve éxito y <b>no crea nada</b>. Confiar en el código
/// de retorno dejaría al técnico creyendo que tiene red de seguridad cuando no la tiene — que es
/// exactamente el escenario en el que se pierde el equipo de un cliente.</para>
///
/// <para>Por eso se cuenta la secuencia más alta antes y después, y solo se considera creado si
/// aumentó.</para>
///
/// <para>Si Restaurar sistema está deshabilitado, se intenta habilitarlo una vez y se reintenta.
/// Dejarlo habilitado es una mejora en sí misma: un equipo sin puntos de restauración no tiene
/// vuelta atrás para nada.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RestorePointService : IRestorePointService
{
    private const string Namespace = @"root\default";
    private const string ClassName = "SystemRestore";

    /// <summary>MODIFY_SETTINGS: el tipo correcto para cambios de configuración.</summary>
    private const uint RestorePointTypeModifySettings = 12;

    /// <summary>BEGIN_SYSTEM_CHANGE.</summary>
    private const uint EventTypeBeginSystemChange = 100;

    private readonly ILogger<RestorePointService> _logger;

    public RestorePointService(ILogger<RestorePointService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<long?> CreateAsync(string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        // Las APIs de WMI son bloqueantes: van a un hilo del pool para no congelar la interfaz.
        return await Task.Run(() => CreateVerified(description), ct).ConfigureAwait(false);
    }

    private long? CreateVerified(string description)
    {
        long before = HighestSequence();

        if (!TryCreate(description, out uint returnValue))
        {
            _logger.LogWarning(
                "CreateRestorePoint devolvió {Code}. Se intenta habilitar Restaurar sistema.", returnValue);

            if (!TryEnable())
            {
                _logger.LogError("No se pudo habilitar Restaurar sistema.");
                return null;
            }

            if (!TryCreate(description, out returnValue))
            {
                _logger.LogError(
                    "CreateRestorePoint falló otra vez con {Code} después de habilitar.", returnValue);
                return null;
            }
        }

        long after = HighestSequence();

        if (after <= before)
        {
            // Éxito reportado sin punto nuevo: es el límite de 24 horas. No se miente al técnico.
            _logger.LogWarning(
                "CreateRestorePoint reportó éxito pero la secuencia no aumentó ({Before} -> {After}). " +
                "Probablemente el límite de frecuencia de 24 h.", before, after);
            return null;
        }

        _logger.LogInformation("Punto de restauración creado y verificado: secuencia {Sequence}.", after);
        return after;
    }

    private bool TryCreate(string description, out uint returnValue)
    {
        returnValue = uint.MaxValue;

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

            returnValue = Convert.ToUInt32(result["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
            return returnValue == 0;
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

            uint code = Convert.ToUInt32(result["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>Secuencia más alta entre los puntos existentes, o 0 si no hay ninguno.</summary>
    private long HighestSequence()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(Namespace),
                new ObjectQuery($"SELECT SequenceNumber FROM {ClassName}"));

            long highest = 0;
            using ManagementObjectCollection results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                try
                {
                    long sequence = Convert.ToInt64(
                        item["SequenceNumber"], System.Globalization.CultureInfo.InvariantCulture);
                    highest = Math.Max(highest, sequence);
                }
                catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException)
                {
                    // Un punto ilegible no invalida el resto.
                }
                finally
                {
                    item.Dispose();
                }
            }

            return highest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "No se pudieron enumerar los puntos de restauración.");
            return 0;
        }
    }
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

                    uint code = Convert.ToUInt32(
                        result["ReturnValue"], System.Globalization.CultureInfo.InvariantCulture);

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
