using System.Runtime.Versioning;
using EasyFix.Core.Classification;
using EasyFix.Core.Diagnostics;
using EasyFix.Core.Rollback;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EasyFix.Core.Fixes;

/// <summary>
/// Desactiva los programas de inicio que la lista blanca autoriza.
/// </summary>
/// <remarks>
/// <para>Es el fix que conecta <see cref="StartupClassifier"/>, que estaba escrito y probado con 20
/// tests pero sin usarse: el diagnóstico contaba los programas de inicio y no había forma de
/// desactivar ninguno.</para>
///
/// <para><b>Cómo desactiva.</b> Escribe el flag de <c>StartupApproved</c>, que es exactamente el
/// mecanismo del Administrador de tareas. <b>No borra la clave <c>Run</c></b>: si la borrara, el
/// programa no aparecería más en la lista y el usuario no podría volver a habilitarlo desde Windows.
/// Así el cambio es visible y reversible desde la propia interfaz del sistema.</para>
///
/// <para><b>Solo Capa 2.</b> Todo lo que no esté en la lista blanca por par (publisher del
/// certificado, producto) queda intacto. Un binario sin firma válida nunca se toca, por más que su
/// nombre coincida.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StartupDisableFix : IFix
{
    /// <summary>
    /// Flag de deshabilitado: 12 bytes, el primero en <c>0x03</c>.
    /// </summary>
    /// <remarks>
    /// Los bytes 4 a 11 son un FILETIME con el momento de la desactivación. El Administrador de tareas
    /// lo usa para mostrar «Deshabilitado» con su fecha; se rellena para que el registro quede
    /// consistente con lo que haría Windows.
    /// </remarks>
    private static byte[] BuildDisabledFlag(DateTimeOffset when)
    {
        var flag = new byte[12];
        flag[0] = 0x03;

        long fileTime = when.ToFileTime();
        BitConverter.GetBytes(fileTime).CopyTo(flag, 4);

        return flag;
    }

    private readonly StartupEntryReader _reader;
    private readonly StartupClassifier _classifier;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupDisableFix> _logger;

    public StartupDisableFix(
        StartupEntryReader reader,
        StartupClassifier classifier,
        TimeProvider time,
        ILogger<StartupDisableFix> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _classifier = classifier;
        _time = time;
        _logger = logger;
    }

    public string Id => "startup.disable";
    public string DisplayName => "Quitar programas del inicio";

    public string Description =>
        "Desactiva los programas de arranque que están en la lista de seguros, identificados por el " +
        "certificado digital de su fabricante. Usa el mismo mecanismo que el Administrador de tareas, " +
        "así que se puede volver a habilitar desde Windows.";

    public FixCategory Category => FixCategory.Performance;
    public FixTier Tier => FixTier.SafeAuto;
    public bool RequiresReboot => false;
    public bool TouchesBootOrDisk => false;
    public bool IsReversible => true;
    public bool IsLongRunning => false;

    public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct)
    {
        // En dominio las GPO reescriben las claves Run: el cambio parece funcionar y se deshace solo.
        if (context.Snapshot.IsDomainJoined)
        {
            return Task.FromResult(FixApplicability.No(
                FixBlockReason.DomainManaged,
                "El equipo está en un dominio: las políticas reescriben los programas de inicio."));
        }

        return Task.FromResult(FixApplicability.Yes());
    }

    /// <summary>
    /// Lee las entradas de inicio, las clasifica y desactiva las autorizadas.
    /// </summary>
    /// <remarks>
    /// <b>Todo va a un hilo del pool.</b> Verificar la firma de cada ejecutable con
    /// <c>X509Chain.Build</c> es lo más lento de la herramienta —accede al almacén de certificados
    /// por cada binario— y hacerlo en el hilo de la interfaz congelaba la ventana entera.
    /// </remarks>
    public Task<FixOutcome> ApplyAsync(
        FixContext context, IProgress<string> log, CancellationToken ct) =>
        Task.Run(() => Apply(context, log, ct), ct);

    private FixOutcome Apply(FixContext context, IProgress<string> log, CancellationToken ct)
    {
        var systemContext = new SystemContext(
            context.Snapshot.IsDomainJoined,
            context.Snapshot.IsSsd,
            context.Snapshot.TotalRamGb ?? 0);

        log.Report("Revisando qué programas arrancan con Windows y quién los firma.");

        IReadOnlyList<StartupEntry> entries = _reader.Read();

        log.Report($"{entries.Count} programa(s) de arranque. Decidiendo cuáles se pueden quitar.");

        var disabled = new List<string>();
        var needApproval = new List<string>();
        var blocked = new List<string>();
        var errors = new List<string>();

        foreach (StartupEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!entry.IsEnabled)
            {
                continue;   // ya estaba deshabilitado
            }

            StartupVerdict verdict = _classifier.Classify(entry.Candidate, systemContext);

            switch (verdict.Tier)
            {
                case ClassificationTier.AutoSafe:
                    if (TryDisable(entry, context, out string? error))
                    {
                        disabled.Add(entry.Candidate.DisplayName);
                        log.Report($"Quitado del arranque: {entry.Candidate.DisplayName}");
                    }
                    else
                    {
                        errors.Add($"{entry.Candidate.DisplayName}: {error}");
                    }

                    break;

                case ClassificationTier.NeedsApproval:
                    needApproval.Add(entry.Candidate.DisplayName);
                    break;

                case ClassificationTier.HardBlocked:
                    blocked.Add(entry.Candidate.DisplayName);
                    break;
            }
        }

        _logger.LogInformation(
            "Inicio: {Total} entradas, {Disabled} desactivadas, {Approval} requieren aprobación, " +
            "{Blocked} intocables, {Errors} con error.",
            entries.Count, disabled.Count, needApproval.Count, blocked.Count, errors.Count);

        if (disabled.Count == 0)
        {
            string why = needApproval.Count > 0
                ? $"Hay {needApproval.Count} programa(s) que podrían quitarse pero necesitan tu " +
                  "confirmación, porque no están en la lista de seguros: " +
                  string.Join(", ", needApproval.Take(5)) + "."
                : "No hay programas de inicio en la lista de seguros.";

            return FixOutcome.NothingToDo(why);
        }

        var summary = new System.Text.StringBuilder();
        summary.Append($"Se quitaron {disabled.Count} programa(s) del inicio: ");
        summary.Append(string.Join(", ", disabled));
        summary.Append('.');

        if (needApproval.Count > 0)
        {
            summary.Append($" Quedan {needApproval.Count} que necesitan tu confirmación.");
        }

        if (blocked.Count > 0)
        {
            summary.Append($" {blocked.Count} son intocables (seguridad, drivers o componentes de Windows).");
        }

        if (errors.Count > 0)
        {
            summary.Append($" {errors.Count} dieron error.");
        }

        return FixOutcome.Applied(summary.ToString());
    }

    /// <summary>
    /// Escribe el flag de deshabilitado, registrando antes el valor anterior.
    /// </summary>
    private bool TryDisable(StartupEntry entry, FixContext context, out string? error)
    {
        error = null;

        bool perUser = entry.Candidate.Location == StartupLocation.RegistryRunCurrentUser;
        RegistryKey root = perUser ? Registry.CurrentUser : Registry.LocalMachine;
        string hive = perUser ? "HKCU" : "HKLM";

        try
        {
            // El journal ANTES de escribir. Si el proceso muere en el medio, el undo sigue sabiendo
            // qué había.
            context.Journal.Append(new JournalAction
            {
                FixId = Id,
                Target = $"{hive}\\{entry.ApprovalKeyPath}\\{entry.ValueName}",
                Reversible = true,
                Undo = new UndoStep
                {
                    Kind = entry.CurrentFlag is null
                        ? UndoKind.RegistryValueDelete
                        : UndoKind.RegistryBinaryValue,
                    Payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["hive"] = hive,
                        ["path"] = entry.ApprovalKeyPath,
                        ["name"] = entry.ValueName,
                        ["valueHex"] = entry.CurrentFlag is null
                            ? string.Empty
                            : Convert.ToHexString(entry.CurrentFlag),
                    },
                },
                Note = $"«{entry.Candidate.DisplayName}» quitado del inicio. " +
                       "Se puede volver a habilitar desde el Administrador de tareas.",
            });

            using RegistryKey? key = root.OpenSubKey(entry.ApprovalKeyPath, writable: true)
                                    ?? root.CreateSubKey(entry.ApprovalKeyPath);

            if (key is null)
            {
                error = $"no se pudo abrir {hive}\\{entry.ApprovalKeyPath}";
                return false;
            }

            key.SetValue(
                entry.ValueName,
                BuildDisabledFlag(_time.GetUtcNow()),
                RegistryValueKind.Binary);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or IOException)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "No se pudo desactivar {Name}.", entry.Candidate.DisplayName);
            return false;
        }
    }
}
