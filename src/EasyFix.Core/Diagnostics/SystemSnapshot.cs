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
    /// <summary>Desgaste en porcentaje. <c>null</c> en desktop: no hay batería, no es un fallo.</summary>
    public double? BatteryWearPercent { get; init; }

    // --- Arranque ---
    /// <summary><c>MainPathBootTime</c> del Event 100, en ms. La mitad del antes/después.</summary>
    public int? MainPathBootTimeMs { get; init; }

    public int? EnabledStartupEntryCount { get; init; }

    /// <summary>Suma de la degradación por app de los eventos 101/103, en ms.</summary>
    public int? StartupDegradationMs { get; init; }

    // --- Contexto que condiciona qué se puede hacer ---
    public bool IsDomainJoined { get; init; }
    public bool BitLockerActive { get; init; }
    public bool HasPrinters { get; init; }
    public bool HasBluetoothAdapter { get; init; }

    /// <summary>Cantidad de antivirus activos. Dos o más = conflicto y lentitud severa.</summary>
    public int ActiveAntivirusCount { get; init; }

    public bool IsSsd => PrimaryDiskMedia is DiskMedia.Ssd or DiskMedia.Scm;
}
