using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Rollback;

/// <summary>
/// Restaura un valor del registro a lo que era antes.
/// </summary>
/// <remarks>
/// Primer <see cref="IUndoHandler"/> concreto. Existe porque el journal ya registraba pasos de
/// deshacer que nadie podía ejecutar: <see cref="UndoEngine"/> los reportaba como «sin handler», que
/// es honesto pero inútil. Hoy lo usa el cambio del límite de frecuencia de puntos de restauración.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryValueUndoHandler : IUndoHandler
{
    private readonly ILogger<RegistryValueUndoHandler> _logger;

    public RegistryValueUndoHandler(ILogger<RegistryValueUndoHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public UndoKind Kind => UndoKind.RegistryValue;

    public Task<bool> UndoAsync(UndoStep step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        string hive = step.Require("hive");
        string path = step.Require("path");
        string name = step.Require("name");
        string kind = step.Payload.TryGetValue("kind", out string? k) ? k : "String";
        string value = step.Require("value");

        try
        {
            using RegistryKey? root = OpenHive(hive)?.OpenSubKey(path, writable: true);
            if (root is null)
            {
                _logger.LogWarning("No se pudo abrir {Hive}\\{Path} para restaurar {Name}.", hive, path, name);
                return Task.FromResult(false);
            }

            if (string.Equals(kind, "DWord", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dword))
                {
                    _logger.LogWarning("'{Value}' no es un DWORD válido para {Name}.", value, name);
                    return Task.FromResult(false);
                }

                root.SetValue(name, dword, RegistryValueKind.DWord);
            }
            else
            {
                root.SetValue(name, value, RegistryValueKind.String);
            }

            _logger.LogInformation("Restaurado {Hive}\\{Path}\\{Name} = {Value}.", hive, path, name, value);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogError(ex, "Falló la restauración de {Hive}\\{Path}\\{Name}.", hive, path, name);
            return Task.FromResult(false);
        }
    }

    internal static RegistryKey? OpenHive(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
        _ => null,
    };
}

/// <summary>
/// Borra un valor del registro que no existía antes de que lo creáramos.
/// </summary>
/// <remarks>
/// Restaurar un valor que antes no estaba dejaría basura en el equipo del cliente y cambiaría su
/// comportamiento de forma silenciosa. Si no existía, deshacer significa borrarlo.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RegistryValueDeleteUndoHandler : IUndoHandler
{
    private readonly ILogger<RegistryValueDeleteUndoHandler> _logger;

    public RegistryValueDeleteUndoHandler(ILogger<RegistryValueDeleteUndoHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public UndoKind Kind => UndoKind.RegistryValueDelete;

    public Task<bool> UndoAsync(UndoStep step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        string hive = step.Require("hive");
        string path = step.Require("path");
        string name = step.Require("name");

        try
        {
            using RegistryKey? root = RegistryValueUndoHandler.OpenHive(hive)?.OpenSubKey(path, writable: true);
            if (root is null)
            {
                // La clave ya no está: el valor tampoco. El objetivo está cumplido.
                _logger.LogInformation(
                    "{Hive}\\{Path} no existe; nada que borrar para {Name}.", hive, path, name);
                return Task.FromResult(true);
            }

            root.DeleteValue(name, throwOnMissingValue: false);

            _logger.LogInformation("Borrado {Hive}\\{Path}\\{Name}.", hive, path, name);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogError(ex, "Falló el borrado de {Hive}\\{Path}\\{Name}.", hive, path, name);
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Reactiva la protección de BitLocker que se había suspendido.
/// </summary>
/// <remarks>
/// La suspensión se hace con <c>DisableKeyProtectors(1)</c>, que se revierte sola en el próximo
/// reinicio. Este handler la reactiva de inmediato, para no dejar el equipo sin protección si el
/// técnico deshace antes de reiniciar.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BitLockerResumeUndoHandler : IUndoHandler
{
    private const string Namespace = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    private readonly ILogger<BitLockerResumeUndoHandler> _logger;

    public BitLockerResumeUndoHandler(ILogger<BitLockerResumeUndoHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public UndoKind Kind => UndoKind.BitLockerResume;

    public Task<bool> UndoAsync(UndoStep step, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        string drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(Namespace),
                new System.Management.ObjectQuery(
                    $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'"));

            using System.Management.ManagementObjectCollection volumes = searcher.Get();

            foreach (System.Management.ManagementBaseObject item in volumes)
            {
                if (item is not System.Management.ManagementObject volume)
                {
                    continue;
                }

                using (volume)
                {
                    using System.Management.ManagementBaseObject result =
                        volume.InvokeMethod("EnableKeyProtectors", null, null);

                    uint code = Convert.ToUInt32(result["ReturnValue"], CultureInfo.InvariantCulture);

                    if (code == 0)
                    {
                        _logger.LogInformation("BitLocker reactivado en {Drive}.", drive);
                        return Task.FromResult(true);
                    }

                    _logger.LogWarning("EnableKeyProtectors devolvió 0x{Code:X8}.", code);
                    return Task.FromResult(false);
                }
            }

            _logger.LogWarning("No se encontró el volumen {Drive} para reactivar BitLocker.", drive);
            return Task.FromResult(false);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Falló la reactivación de BitLocker.");
            return Task.FromResult(false);
        }
    }
}
