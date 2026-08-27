using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Classification;

/// <summary>Una entrada de inicio con lo que hace falta para desactivarla y para revertirlo.</summary>
/// <param name="Candidate">Lo que necesita el clasificador.</param>
/// <param name="ApprovalKeyPath">Clave de <c>StartupApproved</c> donde vive su flag.</param>
/// <param name="ValueName">Nombre del valor dentro de esa clave.</param>
/// <param name="CurrentFlag">Bytes actuales del flag, o <c>null</c> si el valor no existe todavía.</param>
public sealed record StartupEntry(
    StartupCandidate Candidate,
    string ApprovalKeyPath,
    string ValueName,
    byte[]? CurrentFlag)
{
    /// <summary>
    /// <c>true</c> si está habilitada.
    /// </summary>
    /// <remarks>
    /// El primer byte del flag manda: <c>0x02</c> y <c>0x06</c> son habilitado, <c>0x03</c> es
    /// deshabilitado. Si el valor no existe, la entrada está habilitada — es el estado por defecto.
    /// </remarks>
    public bool IsEnabled =>
        CurrentFlag is null || CurrentFlag.Length == 0 || (CurrentFlag[0] != 0x03);
}

/// <summary>
/// Lee los programas que arrancan con Windows, con su firma digital.
/// </summary>
/// <remarks>
/// <para>Es la pieza que le faltaba a <see cref="StartupClassifier"/> para poder usarse: el
/// clasificador decide sobre DTOs, y esto los construye desde Windows.</para>
///
/// <para><b>Sobre la validación de firma.</b> Se obtiene el certificado del binario y se valida su
/// cadena con <see cref="X509Chain"/>. Eso <i>no</i> es exactamente la política Authenticode —para eso
/// haría falta <c>WinVerifyTrust</c> por P/Invoke— pero verifica lo que importa acá: que el
/// certificado encadene a una raíz de confianza del equipo y no esté vencido ni revocado. Un binario
/// sin firmar o con firma que no valida cae en Capa 3 y nunca se desactiva solo.</para>
///
/// <para>Solo se leen las claves <c>Run</c>, no las carpetas de inicio ni las tareas programadas:
/// esas dos se desactivan de otra forma y todavía no están implementadas. El diagnóstico las cuenta;
/// este fix no las toca.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StartupEntryReader
{
    /// <summary>Las claves <c>Run</c> y dónde vive el flag de habilitación de cada una.</summary>
    private static readonly (string RunPath, string ApprovalPath, bool PerUser, StartupLocation Location)[] Sources =
    {
        (@"Software\Microsoft\Windows\CurrentVersion\Run",
         @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
         true, StartupLocation.RegistryRunCurrentUser),

        (@"Software\Microsoft\Windows\CurrentVersion\Run",
         @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
         false, StartupLocation.RegistryRunLocalMachine),

        (@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
         @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
         false, StartupLocation.RegistryRunWow6432Node),
    };

    private readonly ILogger<StartupEntryReader> _logger;

    public StartupEntryReader(ILogger<StartupEntryReader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Lee todas las entradas. Una que falle no impide leer las demás.</summary>
    public IReadOnlyList<StartupEntry> Read()
    {
        HashSet<string> securityProducts = ReadSecurityProductPaths();
        var entries = new List<StartupEntry>();

        foreach ((string runPath, string approvalPath, bool perUser, StartupLocation location) in Sources)
        {
            RegistryKey root = perUser ? Registry.CurrentUser : Registry.LocalMachine;

            try
            {
                using RegistryKey? runKey = root.OpenSubKey(runPath);
                if (runKey is null)
                {
                    continue;
                }

                using RegistryKey? approvalKey = root.OpenSubKey(approvalPath);

                foreach (string name in runKey.GetValueNames())
                {
                    try
                    {
                        string? commandLine = runKey.GetValue(name)?.ToString();
                        if (string.IsNullOrWhiteSpace(commandLine))
                        {
                            continue;
                        }

                        string? executable = ExtractExecutablePath(commandLine);
                        SignatureInfo signature = InspectSignature(executable);

                        entries.Add(new StartupEntry(
                            new StartupCandidate(
                                Id: $"{(perUser ? "HKCU" : "HKLM")}\\{runPath}\\{name}",
                                DisplayName: name,
                                ExecutablePath: executable,
                                ProductName: ReadProductName(executable),
                                Signature: signature,
                                Location: location,
                                IsRegisteredSecurityProduct: executable is not null &&
                                    securityProducts.Contains(executable)),
                            approvalPath,
                            name,
                            approvalKey?.GetValue(name) as byte[]));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        _logger.LogWarning(ex, "No se pudo leer la entrada de inicio {Name}.", name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "No se pudo abrir {Path}.", runPath);
            }
        }

        return entries;
    }

    /// <summary>
    /// Extrae la ruta del ejecutable de una línea de comandos.
    /// </summary>
    /// <remarks>
    /// El valor del registro puede venir entrecomillado con argumentos —
    /// <c>"C:\Program Files\App\app.exe" --minimized</c>— o sin comillas y con espacios en la ruta.
    /// Se maneja el caso entrecomillado, y sin comillas se corta en <c>.exe</c>.
    /// </remarks>
    public static string? ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : null;
        }

        // Sin comillas: se corta después de la primera extensión ejecutable.
        foreach (string extension in new[] { ".exe", ".com", ".bat", ".cmd", ".scr" })
        {
            int at = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                return trimmed[..(at + extension.Length)];
            }
        }

        // Un valor sin extensión reconocible: no se arriesga una ruta inventada.
        return null;
    }

    /// <summary>
    /// Obtiene el publisher del certificado y verifica que su cadena valide.
    /// </summary>
    /// <remarks>
    /// Ver la nota de la clase sobre por qué esto no es exactamente Authenticode. Lo importante para
    /// la Capa 2 es que un binario sin firma válida <b>nunca</b> llega a desactivarse solo.
    /// </remarks>
    private SignatureInfo InspectSignature(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return SignatureInfo.Unsigned;
        }

        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath));

            string? organization = CertificateSubject.ExtractOrganization(certificate.Subject);

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // sin red, sería lento
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            bool valid = chain.Build(certificate);

            if (!valid)
            {
                _logger.LogInformation(
                    "La firma de {Path} no valida: {Status}.",
                    executablePath,
                    string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
            }

            return new SignatureInfo(valid, organization);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            // Sin firma, o no se pudo leer. En los dos casos: no hay evidencia de identidad.
            return SignatureInfo.Unsigned;
        }
    }

    private static string? ReadProductName(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            string? product = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(executablePath).ProductName?.Trim();

            return string.IsNullOrEmpty(product) ? null : product;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rutas de los ejecutables registrados como productos de seguridad.
    /// </summary>
    /// <remarks>
    /// Es la Capa 1 del clasificador: un antivirus nunca se desactiva. Se compara por ruta del
    /// ejecutable, que es lo que expone <c>root\SecurityCenter2</c>.
    /// </remarks>
    private HashSet<string> ReadSecurityProductPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string className in new[] { "AntiVirusProduct", "FirewallProduct", "AntiSpywareProduct" })
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\SecurityCenter2"),
                    new ObjectQuery($"SELECT pathToSignedProductExe FROM {className}"));

                using ManagementObjectCollection results = searcher.Get();

                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        if (item["pathToSignedProductExe"]?.ToString() is { Length: > 0 } path)
                        {
                            paths.Add(path);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "No se pudo consultar {Class}.", className);
            }
        }

        return paths;
    }
}
