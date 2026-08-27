namespace EasyFix.Core.Rollback;

/// <summary>Un punto de restauración leído del sistema.</summary>
/// <param name="Sequence">Número de secuencia.</param>
/// <param name="CreatedUtc">Cuándo se creó. <c>null</c> si la fecha no se pudo interpretar.</param>
/// <param name="Description">Descripción, para el log.</param>
public sealed record RestorePointInfo(long Sequence, DateTimeOffset? CreatedUtc, string? Description);

/// <summary>Veredicto de un intento de verificación.</summary>
public enum RestorePointCheck
{
    /// <summary>Se creó y está verificado.</summary>
    Created,

    /// <summary>Todavía no aparece. La API es asíncrona: hay que volver a mirar.</summary>
    KeepWaiting,

    /// <summary>Se agotaron los intentos sin poder confirmarlo.</summary>
    CannotVerify,
}

/// <summary>
/// Decide si un punto de restauración quedó creado.
/// </summary>
/// <remarks>
/// <para><b>Por qué existe esta clase separada.</b> La versión anterior comparaba la secuencia más
/// alta antes y después de llamar a <c>CreateRestorePoint</c>, y concluía que no se había creado nada.
/// En la primera prueba real eso dio falso negativo en las cinco corridas, aunque las secuencias
/// avanzaban entre corridas (210 → 213 → 214): los puntos <b>sí</b> se estaban creando.</para>
///
/// <para>La causa es que <c>SRSetRestorePoint</c> —la API detrás de <c>CreateRestorePoint</c>— es
/// <b>asíncrona</b>. Enumerar inmediatamente después es una carrera que casi siempre se pierde. La
/// consecuencia práctica fue peor que un fallo: el técnico terminó trabajando sin red de seguridad
/// creyendo que no la tenía, cuando sí existía.</para>
///
/// <para><b>La señal principal es la fecha, no la secuencia.</b> Un punto recién creado se reconoce
/// porque su fecha de creación es de hace segundos. Eso no depende de haber podido leer el estado
/// anterior — que es la otra cosa que falló, porque una consulta WMI que fallaba devolvía cero y cero
/// se interpretaba como "no hay ninguno".</para>
///
/// <para>Sin dependencias de Windows: se testea entera.</para>
/// </remarks>
public static class RestorePointPolicy
{
    /// <summary>
    /// Cuán reciente tiene que ser un punto para considerarlo el que acabamos de crear.
    /// </summary>
    /// <remarks>
    /// Diez minutos es holgado a propósito: cubre relojes desfasados y husos horarios mal
    /// interpretados, y sigue siendo mucho menor que el límite de 24 h de Windows, así que no puede
    /// confundir un punto de ayer con uno nuestro.
    /// </remarks>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(10);

    /// <summary>Cada cuánto volver a mirar mientras se espera.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Cuánto esperar en total antes de darlo por no verificable.</summary>
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    /// <param name="newest">El punto más nuevo que se pudo leer. <c>null</c> = la consulta falló.</param>
    /// <param name="sequenceBefore">
    /// Secuencia más alta antes de crear. <c>null</c> si esa lectura falló — y en ese caso <b>no</b> se
    /// concluye nada: no poder leer no es evidencia de que no exista.
    /// </param>
    /// <param name="now">Ahora, inyectado para poder testear.</param>
    /// <param name="attemptsRemain">Si todavía queda tiempo de sondeo.</param>
    public static RestorePointCheck Evaluate(
        RestorePointInfo? newest,
        long? sequenceBefore,
        DateTimeOffset now,
        bool attemptsRemain)
    {
        // No se pudo leer. Puede ser WMI ocupado justo mientras VSS trabaja: se vuelve a mirar.
        if (newest is null)
        {
            return attemptsRemain ? RestorePointCheck.KeepWaiting : RestorePointCheck.CannotVerify;
        }

        // Señal principal: el punto más nuevo es de hace un rato. Vale aunque la lectura previa haya
        // fallado, que es el caso que antes producía el falso negativo.
        if (newest.CreatedUtc is { } created && created >= now - FreshWindow && created <= now + FreshWindow)
        {
            return RestorePointCheck.Created;
        }

        // Señal de respaldo: la secuencia subió. Sirve cuando la fecha viene ilegible o el reloj del
        // equipo está desfasado más allá de la ventana.
        if (sequenceBefore is { } before && newest.Sequence > before)
        {
            return RestorePointCheck.Created;
        }

        return attemptsRemain ? RestorePointCheck.KeepWaiting : RestorePointCheck.CannotVerify;
    }

    /// <summary>
    /// Interpreta la fecha de WMI, que viene como <c>yyyyMMddHHmmss.ffffff±UUU</c> —
    /// p. ej. <c>20260825130637.000000-300</c>, donde el desplazamiento son <b>minutos</b>.
    /// </summary>
    /// <returns><c>null</c> si no se pudo interpretar, que se trata como "fecha desconocida".</returns>
    public static DateTimeOffset? ParseWmiDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14)
        {
            return null;
        }

        static bool Num(string s, int start, int length, out int result) =>
            int.TryParse(
                s.AsSpan(start, length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result);

        if (!Num(value, 0, 4, out int year) ||
            !Num(value, 4, 2, out int month) ||
            !Num(value, 6, 2, out int day) ||
            !Num(value, 8, 2, out int hour) ||
            !Num(value, 10, 2, out int minute) ||
            !Num(value, 12, 2, out int second))
        {
            return null;
        }

        // El desplazamiento va después del '+' o '-' final, en minutos. Si falta, se asume local.
        TimeSpan offset = TimeSpan.Zero;
        bool hasOffset = false;

        int sign = value.LastIndexOfAny(new[] { '+', '-' });
        if (sign > 14 && Num(value, sign + 1, Math.Min(4, value.Length - sign - 1), out int offsetMinutes))
        {
            offset = TimeSpan.FromMinutes(value[sign] == '-' ? -offsetMinutes : offsetMinutes);
            hasOffset = true;
        }

        try
        {
            return hasOffset
                ? new DateTimeOffset(year, month, day, hour, minute, second, offset)
                : new DateTimeOffset(
                    new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
        }
        catch (ArgumentOutOfRangeException)
        {
            // Fecha imposible: se trata como desconocida en vez de reventar.
            return null;
        }
    }
}

/// <param name="Sequence">Secuencia del punto creado, o <c>null</c> si no se pudo verificar.</param>
/// <param name="ThrottleWasDisabled">
/// Se modificó <c>SystemRestorePointCreationFrequency</c> para poder crear el punto.
/// </param>
/// <param name="PreviousThrottleValue">
/// Valor anterior del límite, para poder revertirlo. <c>null</c> si la clave no existía.
/// </param>
/// <param name="FailureReason">Por qué no se pudo, en español, cuando <see cref="Sequence"/> es null.</param>
public sealed record RestorePointResult(
    long? Sequence,
    bool ThrottleWasDisabled = false,
    int? PreviousThrottleValue = null,
    string? FailureReason = null)
{
    public bool Created => Sequence is not null;
}
