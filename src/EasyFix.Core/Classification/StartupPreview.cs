namespace EasyFix.Core.Classification;

/// <summary>
/// Adelanto de lo que haría «Mejorar rendimiento» con los programas de inicio, calculado durante
/// el análisis — ANTES de que el técnico apriete el botón.
/// </summary>
/// <remarks>
/// <para>Existe por un problema real: en la lista blanca curada (Spotify, Discord, Adobe, Apple,
/// Zoom…) un equipo de oficina típico no tiene casi ningún match, así que «Mejorar rendimiento»
/// desactiva cero programas en muchos casos — correcto por diseño (Capa 3 pide permiso ante lo
/// desconocido), pero sin esta vista previa el técnico lo vive como «no hizo nada» y no tiene forma
/// de saber, antes de probar, si vale la pena ni qué es lo que quedó sin tocar.</para>
///
/// <para>Con nombres: no alcanza con «10 necesitan tu confirmación», hace falta decir cuáles, para
/// que el técnico pueda ir al Administrador de tareas y desactivarlos a mano hoy mismo, sin esperar
/// al Tier B con casillas.</para>
/// </remarks>
/// <param name="AutoDisable">Se desactivan solos con un click, sin pedir nada.</param>
/// <param name="NeedsApproval">Podrían desactivarse pero piden confirmación — zona gris, Capa 3.</param>
/// <param name="Blocked">Intocables: seguridad, driver asociado, o publisher protegido.</param>
/// <param name="BlockedByDomain">
/// El equipo está en un dominio (o no se pudo comprobar): <c>StartupDisableFix.CanApplyAsync</c>
/// bloquea el fix ENTERO antes de clasificar nada, así que ninguna entrada se toca sin importar en
/// qué capa caería. Separado de <see cref="Blocked"/> porque el motivo y el remedio son distintos.
/// </param>
public sealed record StartupPreview(
    IReadOnlyList<string> AutoDisable,
    IReadOnlyList<string> NeedsApproval,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> BlockedByDomain)
{
    public static StartupPreview Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Entradas habilitadas hoy. Las ya desactivadas no cuentan: nada que previsualizar ahí.</summary>
    public int EnabledTotal => AutoDisable.Count + NeedsApproval.Count + Blocked.Count + BlockedByDomain.Count;

    /// <summary>
    /// Clasifica las entradas habilitadas, sin tocar nada. Lógica pura: dadas las mismas entradas y
    /// el mismo contexto, <see cref="StartupClassifier"/> decide igual acá que dentro del fix real.
    /// </summary>
    /// <remarks>
    /// <b>La compuerta de dominio se evalúa PRIMERO, con la misma condición exacta que
    /// <c>StartupDisableFix.CanApplyAsync</c> (<c>IsDomainJoined != false</c>: dominio confirmado O
    /// sin determinar, fallar cerrado).</b> Antes esta vista previa clasificaba sin saber que el fix
    /// completo queda bloqueado en ese caso, así que prometía «se desactivan solos: Spotify, Discord»
    /// en equipos donde al apretar el botón no se tocaba nada — justo la clase de promesa sin medir
    /// que el resto de la v0.9.x elimina en todos lados menos acá.
    /// </remarks>
    public static StartupPreview Build(
        IReadOnlyList<StartupEntry> entries, StartupClassifier classifier, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<StartupEntry> enabled = entries.Where(e => e.IsEnabled).ToList();

        if (context.IsDomainJoined != false)
        {
            return new StartupPreview(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                enabled.Select(e => e.Candidate.DisplayName).ToList());
        }

        var auto = new List<string>();
        var approval = new List<string>();
        var blocked = new List<string>();

        foreach (StartupEntry entry in enabled)
        {
            List<string> bucket = classifier.Classify(entry.Candidate, context).Tier switch
            {
                ClassificationTier.AutoSafe => auto,
                ClassificationTier.NeedsApproval => approval,
                _ => blocked,
            };

            bucket.Add(entry.Candidate.DisplayName);
        }

        return new StartupPreview(auto, approval, blocked, Array.Empty<string>());
    }
}
