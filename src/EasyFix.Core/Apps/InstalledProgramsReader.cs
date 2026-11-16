using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EasyFix.Core.Apps;

/// <summary>Un programa instalado, leído de las claves <c>Uninstall</c> del registro.</summary>
/// <param name="DisplayName">Nombre visible. Nunca vacío: sin nombre no hay forma de reportarlo.</param>
/// <param name="Publisher"><c>null</c> si la clave no lo declara.</param>
/// <param name="EstimatedSizeMb">Tamaño estimado. <c>null</c> = la clave no lo declara, no «cero MB».</param>
/// <param name="RegistryPath">La clave de origen, para el log y para desinstalar a mano.</param>
public sealed record InstalledProgram(
    string DisplayName,
    string? Publisher,
    double? EstimatedSizeMb,
    string RegistryPath);

/// <summary>Puerto para poder testear la evaluación de reputación sin registro de Windows.</summary>
public interface IInstalledProgramsReader
{
    /// <summary>Lista de programas instalados visibles. Lanza si el registro no se puede leer.</summary>
    IReadOnlyList<InstalledProgram> Read();
}

/// <summary>
/// Lee los programas instalados desde las claves <c>Uninstall</c> (HKLM + Wow6432Node + HKCU).
/// </summary>
/// <remarks>
/// <para><b>Nunca <c>Win32_Product</c>.</b> Esa clase WMI dispara una reconfiguración MSI de cada
/// paquete al enumerarse: tarda minutos y puede romper instalaciones. Las claves de registro son la
/// fuente que usa el propio panel de «Aplicaciones instaladas».</para>
///
/// <para>Solo lectura. Contratos SIN VERIFICAR en Windows real (los nombres de valor —
/// <c>DisplayName</c>, <c>Publisher</c>, <c>EstimatedSize</c>, <c>SystemComponent</c>— salen de la
/// documentación y de inspección manual de equipos, no de una corrida de esta clase).</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class InstalledProgramsReader : IInstalledProgramsReader
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallSubKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> Read()
    {
        var programs = new List<InstalledProgram>();

        foreach ((RegistryKey Hive, string SubKey, string Label) root in new[]
                 {
                     (Registry.LocalMachine, UninstallSubKey, @"HKLM\" + UninstallSubKey),
                     (Registry.LocalMachine, UninstallSubKey32, @"HKLM\" + UninstallSubKey32),
                     (Registry.CurrentUser, UninstallSubKey, @"HKCU\" + UninstallSubKey),
                 })
        {
            using RegistryKey? key = root.Hive.OpenSubKey(root.SubKey);
            if (key is null)
            {
                continue; // Wow6432Node no existe en Windows de 32 bits: ausente, no error.
            }

            foreach (string name in key.GetSubKeyNames())
            {
                using RegistryKey? entry = key.OpenSubKey(name);
                if (entry is null)
                {
                    continue;
                }

                // SystemComponent = 1 marca entradas que el panel de aplicaciones oculta
                // (redistribuibles, componentes internos). Se respeta el mismo filtro. Algunos
                // instaladores lo escriben como texto "1" en vez de DWORD; cuenta igual.
                object? systemComponent = entry.GetValue("SystemComponent");
                if (systemComponent is int sc && sc == 1 ||
                    systemComponent is string scText && scText.Trim() == "1")
                {
                    continue;
                }

                if (entry.GetValue("DisplayName") is not string displayName ||
                    string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                // EstimatedSize está en KB (DWORD). Un valor ausente queda null, jamás 0.
                double? sizeMb = entry.GetValue("EstimatedSize") is int kb && kb > 0
                    ? kb / 1024.0
                    : null;

                programs.Add(new InstalledProgram(
                    displayName.Trim(),
                    entry.GetValue("Publisher") as string is { Length: > 0 } publisher ? publisher.Trim() : null,
                    sizeMb,
                    $@"{root.Label}\{name}"));
            }
        }

        return programs;
    }
}
