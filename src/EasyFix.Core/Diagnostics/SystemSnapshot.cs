using EasyFix.Core.Network;

namespace EasyFix.Core.Diagnostics;

/// <summary>Tipo de medio del disco. Los valores replican <c>MSFT_PhysicalDisk.MediaType</c>.</summary>
public enum DiskMedia
{
    /// <summary>El controlador no lo informa. Pasa con varios RAID; hay que caer al plan B (SpindleSpeed).</summary>
    Unknown = 0,

    Hdd = 3,
    Ssd = 4,

    /// <summary>Storage Class Memory (Optane y similares).</summary>
    Scm = 5,
}

/// <summary>Salud del disco según SMART.</summary>
public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,

    /// <summary>
    /// SMART predice falla. Dispara el modo respaldo: se bloquean los fixes. Optimizar un disco que
    /// se está muriendo es la peor jugada posible.
    /// </summary>
    Failing,
}

/// <summary>
/// Lo que el escaneo rápido midió del equipo. Es la entrada de <see cref="HardwareAdvisor"/>.
/// </summary>
/// <remarks>
/// <para>Todo lo opcional es <c>null</c> cuando no se pudo medir —un chequeo que dio timeout, una
/// clase WMI que no existe en esa versión de Windows, un desktop sin batería—. Nunca se usa un cero
/// o un valor centinela para "no sé": haría que el motor de recomendaciones dijera cosas falsas con
/// total seguridad.</para>
///
/// <para>El motor de recomendaciones trabaja sobre este tipo, no sobre texto formateado. Así se
/// testea con números y no parseando cadenas.</para>
/// </remarks>
public sealed record SystemSnapshot
{
    // --- Disco ---
    public DiskMedia PrimaryDiskMedia { get; init; } = DiskMedia.Unknown;
    public DiskHealth PrimaryDiskHealth { get; init; } = DiskHealth.Unknown;
    public double? SystemDriveFreePercent { get; init; }
    public double? SystemDriveTotalGb { get; init; }

    /// <summary>Latencia media por transferencia, en ms. Por encima del umbral, el disco es el cuello de botella.</summary>
    public double? DiskLatencyMs { get; init; }

    // --- Memoria ---
    public double? TotalRamGb { get; init; }

    /// <summary>Slots físicos libres, de <c>MemoryDevices</c> menos los módulos instalados.</summary>
    public int? FreeMemorySlots { get; init; }

    /// <summary>Tipo del módulo instalado: <c>"DDR3"</c>, <c>"DDR4"</c>, <c>"DDR5"</c>.</summary>
    public string? MemoryType { get; init; }

    public int? MemorySpeedMhz { get; init; }

    /// <summary>Porcentaje de commit usado. Por encima del umbral, el equipo está usando disco como memoria.</summary>
    public double? CommitUsedPercent { get; init; }

    // --- CPU ---
    public string? CpuName { get; init; }
    public int? CpuCoreCount { get; init; }

    /// <summary>Frecuencia actual como porcentaje del máximo. Bajo y sostenido = throttling térmico.</summary>
    public double? CpuPercentOfMaxFrequency { get; init; }

    // --- Batería ---
    /// <summary>
    /// <c>true</c> = WMI enumeró al menos una batería, <c>false</c> = la consulta terminó sin
    /// ninguna, <c>null</c> = la sonda no corrió.
    /// </summary>
    /// <remarks>
    /// Lo medido es «WMI ve una batería», no «es una laptop»: un desktop con UPS USB también expone
    /// <c>Win32_Battery</c>, y una laptop con la batería extraída o el driver ACPI caído no expone
    /// ninguna. Quien consuma este campo tiene que hablar de «batería presente», nunca afirmar el
    /// tipo de equipo.
    /// </remarks>
    public bool? HasBattery { get; init; }

    /// <summary>Desgaste en porcentaje. <c>null</c> cuando no hay batería o no se pudo medir.</summary>
    public double? BatteryWearPercent { get; init; }

    // --- Arranque ---
    /// <summary><c>MainPathBootTime</c> del Event 100, en ms. La mitad del antes/después.</summary>
    public int? MainPathBootTimeMs { get; init; }

    public int? EnabledStartupEntryCount { get; init; }

    /// <summary>Suma de la degradación por app de los eventos 101/103, en ms.</summary>
    public int? StartupDegradationMs { get; init; }

    // --- Contexto que condiciona qué se puede hacer ---
    // Todos nullable: un timeout de sonda NO es un «false». Un bool que afirma «no hay BitLocker»
    // cuando en realidad no se miró es exactamente la clase de defecto que produjo v0.5.0–v0.8.0.
    // Quien consume estos campos decide qué hacer con null, y la regla es fallar cerrado:
    // BitLocker desconocido bloquea igual que BitLocker activo; impresora desconocida protege
    // el Spooler igual que impresora presente.
    public bool? IsDomainJoined { get; init; }
    public bool? BitLockerActive { get; init; }
    public bool? HasPrinters { get; init; }
    public bool? HasBluetoothAdapter { get; init; }

    /// <summary>Cantidad de antivirus activos. Dos o más = conflicto y lentitud severa. <c>null</c> = no medido.</summary>
    public int? ActiveAntivirusCount { get; init; }

    /// <summary>
    /// Nombres de los antivirus con protección en tiempo real activa, tal como los reporta
    /// SecurityCenter2. Permite decir CUÁLES sobran, no solo cuántos hay.
    /// </summary>
    public IReadOnlyList<string>? ActiveAntivirusNames { get; init; }

    /// <summary>
    /// Lo que dice el predictor SMART, separado de <see cref="PrimaryDiskHealth"/>.
    /// </summary>
    /// <remarks>
    /// Antes un SMART negativo «ascendía» <see cref="DiskHealth.Unknown"/> a Healthy, así que un
    /// timeout de <c>disk.physical</c> terminaba reportando salud que nadie midió. SMART negativo
    /// solo afirma «el predictor no anticipa falla» — no es un estado general de salud.
    /// </remarks>
    public bool? SmartPredictsFailure { get; init; }

    /// <summary>
    /// Estado de la conexión a internet. <c>null</c> = no se comprobó.
    /// </summary>
    /// <remarks>
    /// Condiciona el botón de instalar programas: todo se descarga en el momento, así que sin conexión
    /// no hay nada que instalar. Está en el snapshot para que el reporte lo diga <b>antes</b> de que el
    /// técnico apriete el botón — en el equipo del 2026-08-29 se enteró después de cuatro fallos, y con
    /// el motivo equivocado.
    /// </remarks>
    public ConnectivityStatus? Network { get; init; }

    /// <summary>
    /// <c>true</c> = SSD u Optane, <c>false</c> = mecánico, <c>null</c> = no se pudo identificar.
    /// </summary>
    /// <remarks>
    /// Era <c>bool</c> y devolvía <c>false</c> para <see cref="DiskMedia.Unknown"/>: un disco sin
    /// identificar se trataba como mecánico, lo que suprimía la recomendación de SSD —la más valiosa
    /// de la herramienta— y afectaba la condición de SysMain. Un disco no identificado no es un HDD.
    /// </remarks>
    public bool? IsSsd => PrimaryDiskMedia switch
    {
        DiskMedia.Ssd or DiskMedia.Scm => true,
        DiskMedia.Hdd => false,
        _ => null,
    };

    /// <summary>
    /// Todo lo medido en una línea por campo, para el log.
    /// </summary>
    /// <remarks>
    /// Sin esto el log solo hablaba cuando algo fallaba, así que no servía para confirmar que el
    /// diagnóstico había funcionado — que era justamente lo que faltaba verificar. Los campos sin
    /// medir salen como «no medido», nunca como cero.
    /// </remarks>
    public string ToLogSummary()
    {
        static string N(double? v, string unit = "") =>
            v is double d ? $"{d:0.##}{unit}" : "no medido";

        static string I(int? v) =>
            v is int i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no medido";

        static string B(bool? v) => v switch
        {
            true => "True",
            false => "False",
            null => "no medido",
        };

        return string.Join(Environment.NewLine, new[]
        {
            $"  disco.tipo            = {PrimaryDiskMedia}",
            $"  disco.salud           = {PrimaryDiskHealth}",
            $"  disco.smart_falla     = {B(SmartPredictsFailure)}",
            $"  disco.libre%          = {N(SystemDriveFreePercent, " %")}",
            $"  disco.total           = {N(SystemDriveTotalGb, " GB")}",
            $"  disco.latencia        = {N(DiskLatencyMs, " ms")}",
            $"  ram.total             = {N(TotalRamGb, " GB")}",
            $"  ram.slots_libres      = {I(FreeMemorySlots)}",
            $"  ram.tipo              = {MemoryType ?? "no medido"}",
            $"  ram.velocidad         = {I(MemorySpeedMhz)}",
            $"  ram.commit%           = {N(CommitUsedPercent, " %")}",
            $"  cpu.nombre            = {CpuName ?? "no medido"}",
            $"  cpu.nucleos           = {I(CpuCoreCount)}",
            $"  cpu.frecuencia%       = {N(CpuPercentOfMaxFrequency, " %")}",
            $"  bateria.presente      = {B(HasBattery)}",
            $"  bateria.desgaste      = {N(BatteryWearPercent, " %")}",
            $"  arranque.ms           = {I(MainPathBootTimeMs)}",
            $"  arranque.programas    = {I(EnabledStartupEntryCount)}",
            $"  arranque.retraso_ms   = {I(StartupDegradationMs)}",
            $"  dominio               = {B(IsDomainJoined)}",
            $"  bitlocker             = {B(BitLockerActive)}",
            $"  impresoras            = {B(HasPrinters)}",
            $"  bluetooth             = {B(HasBluetoothAdapter)}",
            $"  antivirus_activos     = {I(ActiveAntivirusCount)}",
            $"  antivirus_nombres     = {(ActiveAntivirusNames is { Count: > 0 } avs ? string.Join(" | ", avs) : ActiveAntivirusNames is null ? "no medido" : "ninguno")}",
            $"  internet              = {Network?.ToString() ?? "no comprobado"}",
        });
    }
}
