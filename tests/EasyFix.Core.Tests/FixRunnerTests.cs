using EasyFix.Core.Diagnostics;
using EasyFix.Core.Fixes;
using EasyFix.Core.Rollback;
using EasyFix.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyFix.Core.Tests;

/// <summary>
/// Tests de las compuertas de seguridad. Es la política que decide si se toca el equipo de un
/// cliente, así que los casos que importan son los de NO tocar.
/// </summary>
public sealed class FixRunnerTests
{
    private sealed class FakeRestorePoints : IRestorePointService
    {
        private readonly long? _sequence;
        private readonly Exception? _throws;
        private readonly bool _throttleDisabled;
        private readonly int? _previousThrottle;

        public FakeRestorePoints(
            long? sequence,
            Exception? throws = null,
            bool throttleDisabled = false,
            int? previousThrottle = null)
        {
            _sequence = sequence;
            _throws = throws;
            _throttleDisabled = throttleDisabled;
            _previousThrottle = previousThrottle;
        }

        public int Calls { get; private set; }

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
        {
            Calls++;

            if (_throws is not null)
            {
                throw _throws;
            }

            return Task.FromResult(new RestorePointResult(
                _sequence,
                _throttleDisabled,
                _previousThrottle,
                _sequence is null ? "El punto no apareció en el tiempo esperado." : null));
        }
    }

    private sealed class FakeBitLocker : IBitLockerService
    {
        private readonly bool _succeeds;
        public FakeBitLocker(bool succeeds) => _succeeds = succeeds;
        public int Calls { get; private set; }

        public Task<bool> SuspendUntilNextRebootAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_succeeds);
        }
    }

    /// <summary>Fix de prueba: registra si lo aplicaron y devuelve lo que se le indique.</summary>
    private sealed class FakeFix : IFix
    {
        private readonly FixOutcome _outcome;
        private readonly FixApplicability _applicability;
        private readonly Exception? _throws;

        public FakeFix(
            string id,
            FixTier tier = FixTier.SafeAuto,
            bool touchesBootOrDisk = false,
            FixOutcome? outcome = null,
            FixApplicability? applicability = null,
            Exception? throws = null)
        {
            Id = id;
            Tier = tier;
            TouchesBootOrDisk = touchesBootOrDisk;
            _outcome = outcome ?? FixOutcome.Applied($"{id} aplicado");
            _applicability = applicability ?? FixApplicability.Yes();
            _throws = throws;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Description => Id;
        public FixTier Tier { get; }
        public bool RequiresReboot => false;
        public bool TouchesBootOrDisk { get; }
        public bool IsReversible => true;
        public bool IsLongRunning => false;

        public bool WasApplied { get; private set; }

        public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct) =>
            Task.FromResult(_applicability);

        public Task<FixOutcome> ApplyAsync(FixContext context, IProgress<string> log, CancellationToken ct)
        {
            WasApplied = true;
            return _throws is not null ? throw _throws : Task.FromResult(_outcome);
        }
    }

    private static readonly SystemSnapshot HealthyPc = new()
    {
        PrimaryDiskMedia = DiskMedia.Ssd,
        PrimaryDiskHealth = DiskHealth.Healthy,
        TotalRamGb = 16,
    };

    private static (FixRunner Runner, FakeRestorePoints Points, FakeBitLocker BitLocker) Build(
        long? restoreSequence = 42, bool bitLockerSucceeds = true)
    {
        var points = new FakeRestorePoints(restoreSequence);
        var bitLocker = new FakeBitLocker(bitLockerSucceeds);
        return (new FixRunner(points, bitLocker, NullLogger<FixRunner>.Instance), points, bitLocker);
    }

    private static (RunJournal Journal, ListJournalSink Sink) Journal()
    {
        var sink = new ListJournalSink();
        return (RunJournal.Start(new JournalHeader { RunId = "test" }, sink), sink);
    }

    // ---- Compuerta 1: disco fallando -------------------------------------------------------

    [Fact]
    public async Task DiscoFallando_AbortaSinTocarNada()
    {
        // Escribir en un disco que se está muriendo acelera la pérdida de datos. Es la primera
        // compuerta a propósito: se evalúa antes incluso de crear el punto de restauración.
        (FixRunner runner, FakeRestorePoints points, _) = Build();
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("cualquiera");

        RunResult result = await runner.RunAsync(
            new[] { fix },
            HealthyPc with { PrimaryDiskHealth = DiskHealth.Failing },
            null, journal, bitLockerKeyConfirmed: false);

        Assert.True(result.Aborted);
        Assert.Contains("disco está fallando", result.AbortReason!, StringComparison.Ordinal);
        Assert.False(fix.WasApplied);
        Assert.Equal(0, points.Calls);   // ni siquiera se intentó el punto de restauración
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task DiscoFallando_MarcaLaCorridaComoAbortadaEnElJournal()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(
            Array.Empty<IFix>(),
            HealthyPc with { PrimaryDiskHealth = DiskHealth.Failing },
            null, journal, false);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.Equal(RunState.Aborted, loaded.State);
    }

    // ---- Compuerta 2: punto de restauración -------------------------------------------------

    [Fact]
    public async Task SinPuntoDeRestauracion_AbortaSinAplicarNada()
    {
        // Sin red de seguridad no se toca nada. Es la regla que hace reversible a la herramienta.
        (FixRunner runner, _, _) = Build(restoreSequence: null);
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("cualquiera");

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.True(result.Aborted);
        Assert.Contains("punto de restauración", result.AbortReason!, StringComparison.Ordinal);
        Assert.False(fix.WasApplied);
        Assert.Null(result.RestorePointSequence);
    }

    [Fact]
    public async Task PuntoDeRestauracionQueRevienta_SeTrataComoNoCreado()
    {
        var points = new FakeRestorePoints(null, throws: new InvalidOperationException("WMI caído"));
        var runner = new FixRunner(points, new FakeBitLocker(true), NullLogger<FixRunner>.Instance);
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("cualquiera");

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.True(result.Aborted);
        Assert.False(fix.WasApplied);
    }

    [Fact]
    public async Task ConPuntoDeRestauracion_AplicaYDevuelveLaSecuencia()
    {
        (FixRunner runner, _, _) = Build(restoreSequence: 42);
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("limpieza");

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.False(result.Aborted);
        Assert.Equal(42, result.RestorePointSequence);
        Assert.True(fix.WasApplied);
        Assert.Single(result.Applied);
    }

    [Fact]
    public async Task SinPuntoDeRestauracionPeroConOverride_AplicaIgual()
    {
        // Salió de la primera prueba real: en muchos equipos Restaurar sistema viene deshabilitado
        // de fábrica, y abortar por eso dejaba la herramienta inservible. El override es una
        // decisión explícita del técnico, nunca el default.
        (FixRunner runner, _, _) = Build(restoreSequence: null);
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("limpieza");

        RunResult result = await runner.RunAsync(
            new[] { fix }, HealthyPc, null, journal,
            bitLockerKeyConfirmed: false, allowWithoutRestorePoint: true);

        Assert.False(result.Aborted);
        Assert.True(fix.WasApplied);
        Assert.True(result.RanWithoutRestorePoint);
        Assert.Null(result.RestorePointSequence);
    }

    [Fact]
    public async Task ConOverride_QuedaRegistradoEnElJournalQueNoHuboRedDeSeguridad()
    {
        (FixRunner runner, _, _) = Build(restoreSequence: null);
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(
            new[] { new FakeFix("a") }, HealthyPc, null, journal, false, allowWithoutRestorePoint: true);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);

        // El journal es la constancia de que se trabajó sin respaldo del sistema.
        Assert.Contains(loaded.Actions, a => a.FixId == "restorepoint.skipped");
        Assert.Equal(RunState.Completed, loaded.State);
    }

    [Fact]
    public async Task ConOverride_ElUndoSigueFuncionando()
    {
        // Es el punto: sin punto de restauración se pierde el respaldo del SISTEMA, pero cada cambio
        // se sigue registrando, así que «Deshacer todo» funciona igual.
        (FixRunner runner, _, _) = Build(restoreSequence: null);
        (RunJournal journal, ListJournalSink sink) = Journal();

        journal.Append(new JournalAction
        {
            FixId = "startup.disable",
            Target = "Spotify",
            Reversible = true,
            Undo = new UndoStep { Kind = UndoKind.RegistryBinaryValue },
        });

        await runner.RunAsync(
            new[] { new FakeFix("a") }, HealthyPc, null, journal, false, allowWithoutRestorePoint: true);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.True(loaded.HasReversibleActions);
    }

    [Fact]
    public async Task OverrideNoSaltaLaCompuertaDelDiscoFallando()
    {
        // El override es solo para el punto de restauración. Un disco muriendo sigue abortando:
        // continuar ahí no es una decisión que corresponda ofrecer.
        (FixRunner runner, _, _) = Build(restoreSequence: null);
        (RunJournal journal, _) = Journal();
        var fix = new FakeFix("cualquiera");

        RunResult result = await runner.RunAsync(
            new[] { fix },
            HealthyPc with { PrimaryDiskHealth = DiskHealth.Failing },
            null, journal, false, allowWithoutRestorePoint: true);

        Assert.True(result.Aborted);
        Assert.False(fix.WasApplied);
    }

    [Fact]
    public async Task ConPuntoDeRestauracionYOverride_NoMarcaQueCorrioSinRed()
    {
        (FixRunner runner, _, _) = Build(restoreSequence: 42);
        (RunJournal journal, _) = Journal();

        RunResult result = await runner.RunAsync(
            new[] { new FakeFix("a") }, HealthyPc, null, journal, false, allowWithoutRestorePoint: true);

        Assert.False(result.RanWithoutRestorePoint);
        Assert.Equal(42, result.RestorePointSequence);
    }

    [Fact]
    public async Task SiSeQuitoElLimiteDeFrecuencia_QuedaRegistradoParaPoderRevertirlo()
    {
        // Es el único cambio de configuración del sistema que hace la herramienta para poder
        // funcionar. Tiene que quedar en el journal con su valor anterior.
        var points = new FakeRestorePoints(42, throttleDisabled: true, previousThrottle: 1440);
        var runner = new FixRunner(points, new FakeBitLocker(true), NullLogger<FixRunner>.Instance);
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(new[] { new FakeFix("a") }, HealthyPc, null, journal, false);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        JournalAction throttle = Assert.Single(loaded.Actions, a => a.FixId == "restorepoint.throttle");

        Assert.True(throttle.Reversible);
        Assert.Equal(UndoKind.RegistryValue, throttle.Undo!.Kind);
        Assert.Equal("1440", throttle.Undo.Require("value"));
    }

    [Fact]
    public async Task SiElLimiteNoExistia_ElUndoLoBorraEnVezDeRestaurarUnValor()
    {
        var points = new FakeRestorePoints(42, throttleDisabled: true, previousThrottle: null);
        var runner = new FixRunner(points, new FakeBitLocker(true), NullLogger<FixRunner>.Instance);
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(new[] { new FakeFix("a") }, HealthyPc, null, journal, false);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        JournalAction throttle = Assert.Single(loaded.Actions, a => a.FixId == "restorepoint.throttle");

        // Restaurar un valor que no existía dejaría basura: hay que borrarlo.
        Assert.Equal(UndoKind.RegistryValueDelete, throttle.Undo!.Kind);
    }

    [Fact]
    public async Task SiNoSeTocoElLimite_NoSeRegistraNada()
    {
        var points = new FakeRestorePoints(42, throttleDisabled: false);
        var runner = new FixRunner(points, new FakeBitLocker(true), NullLogger<FixRunner>.Instance);
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(new[] { new FakeFix("a") }, HealthyPc, null, journal, false);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.DoesNotContain(loaded.Actions, a => a.FixId == "restorepoint.throttle");
    }

    [Fact]
    public async Task ElMotivoRealDelFalloLlegaAlMensajeDeAborto()
    {
        // El mensaje genérico anterior decía siempre lo mismo. Ahora se propaga lo que informó el
        // servicio, que es lo que permite distinguir "VSS detenido" de "sin espacio".
        var points = new FakeRestorePoints(null);
        var runner = new FixRunner(points, new FakeBitLocker(true), NullLogger<FixRunner>.Instance);
        (RunJournal journal, _) = Journal();

        RunResult result = await runner.RunAsync(
            new[] { new FakeFix("a") }, HealthyPc, null, journal, false);

        Assert.True(result.Aborted);
        Assert.Contains("no apareció en el tiempo esperado", result.AbortReason!, StringComparison.Ordinal);
    }

    // ---- Compuerta 3: BitLocker -------------------------------------------------------------

    [Fact]
    public async Task BitLockerSinConfirmar_BloqueaSoloLosFixesQueTocanArranqueODisco()
    {
        // El peor daño posible: dejar al cliente fuera de su propio equipo. Pero el resto de las
        // mejoras corre normal — bloquear todo sería excesivo.
        (FixRunner runner, _, FakeBitLocker bitLocker) = Build();
        (RunJournal journal, _) = Journal();

        var safeFix = new FakeFix("temporales", touchesBootOrDisk: false);
        var riskyFix = new FakeFix("chkdsk", touchesBootOrDisk: true);

        RunResult result = await runner.RunAsync(
            new IFix[] { safeFix, riskyFix },
            HealthyPc with { BitLockerActive = true },
            null, journal, bitLockerKeyConfirmed: false);

        Assert.False(result.Aborted);
        Assert.True(safeFix.WasApplied);
        Assert.False(riskyFix.WasApplied);
        Assert.Equal(0, bitLocker.Calls);   // no se suspende sin confirmación

        FixReport blocked = Assert.Single(result.BlockedFixes);
        Assert.Equal("chkdsk", blocked.FixId);
        Assert.Equal(FixBlockReason.BitLockerUnconfirmed, blocked.Blocked!.Reason);
    }

    [Fact]
    public async Task BitLockerConfirmado_SuspendeYAplicaTodo()
    {
        (FixRunner runner, _, FakeBitLocker bitLocker) = Build(bitLockerSucceeds: true);
        (RunJournal journal, ListJournalSink sink) = Journal();
        var riskyFix = new FakeFix("chkdsk", touchesBootOrDisk: true);

        RunResult result = await runner.RunAsync(
            new IFix[] { riskyFix },
            HealthyPc with { BitLockerActive = true },
            null, journal, bitLockerKeyConfirmed: true);

        Assert.True(riskyFix.WasApplied);
        Assert.Equal(1, bitLocker.Calls);

        // Y queda registrado para poder reactivarlo.
        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.Contains(loaded.Actions, a => a.Undo?.Kind == UndoKind.BitLockerResume);
    }

    [Fact]
    public async Task BitLockerConfirmadoPeroNoSePudoSuspender_BloqueaIgual()
    {
        // Falla cerrado: mejor no reparar que arriesgarse a que pida la clave de recuperación.
        (FixRunner runner, _, _) = Build(bitLockerSucceeds: false);
        (RunJournal journal, _) = Journal();
        var riskyFix = new FakeFix("chkdsk", touchesBootOrDisk: true);

        RunResult result = await runner.RunAsync(
            new IFix[] { riskyFix },
            HealthyPc with { BitLockerActive = true },
            null, journal, bitLockerKeyConfirmed: true);

        Assert.False(riskyFix.WasApplied);
        Assert.Single(result.BlockedFixes);
    }

    [Fact]
    public async Task SinBitLocker_NoSeIntentaSuspenderNada()
    {
        (FixRunner runner, _, FakeBitLocker bitLocker) = Build();
        (RunJournal journal, _) = Journal();
        var riskyFix = new FakeFix("chkdsk", touchesBootOrDisk: true);

        await runner.RunAsync(new IFix[] { riskyFix }, HealthyPc, null, journal, false);

        Assert.True(riskyFix.WasApplied);
        Assert.Equal(0, bitLocker.Calls);
    }

    // ---- Compuerta 4: dominio ---------------------------------------------------------------

    [Fact]
    public async Task EnDominio_BloqueaLosFixesQueRequierenAprobacion()
    {
        // Las GPO revierten los cambios: el arreglo parece funcionar y se deshace solo.
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var autoFix = new FakeFix("temporales", FixTier.SafeAuto);
        var approvalFix = new FakeFix("servicios", FixTier.RequiresApproval);

        RunResult result = await runner.RunAsync(
            new IFix[] { autoFix, approvalFix },
            HealthyPc with { IsDomainJoined = true },
            null, journal, false);

        Assert.True(autoFix.WasApplied);
        Assert.False(approvalFix.WasApplied);
        Assert.Equal(FixBlockReason.DomainManaged, Assert.Single(result.BlockedFixes).Blocked!.Reason);
    }

    // ---- El fix decide si le corresponde ----------------------------------------------------

    [Fact]
    public async Task FixQueDiceQueNoCorresponde_NoSeAplica_YSeReporta()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var fix = new FakeFix("memoria",
            applicability: FixApplicability.No(FixBlockReason.NotNeeded, "Nada apunta a la memoria."));

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.False(fix.WasApplied);
        Assert.Equal(FixBlockReason.NotNeeded, Assert.Single(result.BlockedFixes).Blocked!.Reason);
    }

    [Fact]
    public async Task ComprobacionPreviaQueRevienta_NoAplica_YNoCortaLaCorrida()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var broken = new FakeFix("roto") { };
        var brokenCheck = new BrokenCheckFix();
        var good = new FakeFix("bueno");

        RunResult result = await runner.RunAsync(
            new IFix[] { brokenCheck, good }, HealthyPc, null, journal, false);

        Assert.True(good.WasApplied);
        Assert.Equal(FixBlockReason.Undetermined, Assert.Single(result.BlockedFixes).Blocked!.Reason);
    }

    private sealed class BrokenCheckFix : IFix
    {
        public string Id => "comprobacion-rota";
        public string DisplayName => Id;
        public string Description => Id;
        public FixTier Tier => FixTier.SafeAuto;
        public bool RequiresReboot => false;
        public bool TouchesBootOrDisk => false;
        public bool IsReversible => false;
        public bool IsLongRunning => false;

        public Task<FixApplicability> CanApplyAsync(FixContext context, CancellationToken ct) =>
            throw new InvalidOperationException("WMI no responde");

        public Task<FixOutcome> ApplyAsync(FixContext c, IProgress<string> l, CancellationToken ct) =>
            Task.FromResult(FixOutcome.Applied("nunca llega acá"));
    }

    // ---- Fallos y reinicio ------------------------------------------------------------------

    [Fact]
    public async Task FixQueRevienta_NoCortaLosDemas()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var boom = new FakeFix("explota", throws: new InvalidOperationException("kaboom"));
        var after = new FakeFix("despues");

        RunResult result = await runner.RunAsync(
            new IFix[] { boom, after }, HealthyPc, null, journal, false);

        Assert.True(after.WasApplied);
        Assert.Single(result.Failed);
        Assert.Contains("kaboom", result.Failed.First().Outcome!.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixQueNecesitaReinicio_MarcaLaCorridaComoPendiente()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, ListJournalSink sink) = Journal();

        var fix = new FakeFix("sfc", outcome: FixOutcome.NeedsReboot("Hace falta reiniciar."));

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.True(result.RebootRequired);

        LoadedJournal loaded = JournalReader.Parse(sink.Lines);
        Assert.Equal(RunState.AwaitingReboot, loaded.State);
        Assert.Contains("sfc", loaded.PendingFixIds);
    }

    [Fact]
    public async Task SinFixesPendientes_LaCorridaQuedaCompletada()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, ListJournalSink sink) = Journal();

        await runner.RunAsync(new[] { new FakeFix("a") }, HealthyPc, null, journal, false);

        Assert.Equal(RunState.Completed, JournalReader.Parse(sink.Lines).State);
    }

    [Fact]
    public async Task SumaLosBytesLiberados()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var a = new FakeFix("a", outcome: FixOutcome.Applied("ok", 1000));
        var b = new FakeFix("b", outcome: FixOutcome.Applied("ok", 2000));

        RunResult result = await runner.RunAsync(new IFix[] { a, b }, HealthyPc, null, journal, false);

        Assert.Equal(3000, result.TotalFreedBytes);
    }

    [Fact]
    public async Task NothingToDo_NoCuentaComoFallo()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();

        var fix = new FakeFix("sfc", outcome: FixOutcome.NothingToDo("Ya estaba íntegro."));

        RunResult result = await runner.RunAsync(new[] { fix }, HealthyPc, null, journal, false);

        Assert.Empty(result.Failed);
        Assert.True(fix.WasApplied);
    }

    [Fact]
    public async Task Cancelacion_SePropaga()
    {
        (FixRunner runner, _, _) = Build();
        (RunJournal journal, _) = Journal();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(new[] { new FakeFix("a") }, HealthyPc, null, journal, false, false, null, cts.Token));
    }

    [Fact]
    public async Task ListaVacia_CreaElPuntoDeRestauracionIgual()
    {
        // Es intencional: la corrida queda registrada con su punto, aunque no haya nada que aplicar.
        (FixRunner runner, FakeRestorePoints points, _) = Build();
        (RunJournal journal, _) = Journal();

        RunResult result = await runner.RunAsync(Array.Empty<IFix>(), HealthyPc, null, journal, false);

        Assert.False(result.Aborted);
        Assert.Equal(1, points.Calls);
        Assert.Empty(result.Results);
    }
}
