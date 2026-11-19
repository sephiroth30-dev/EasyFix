namespace EasyFix.Core.Drivers;

/// <summary>
/// Qué tan seguro es aplicar una actualización de driver sin que un técnico la revise.
/// </summary>
/// <remarks>
/// Es una <b>lista blanca</b>, no una lista negra: <see cref="DriverRiskClassifier"/> solo marca
/// <see cref="Safe"/> lo que matchea una clase de dispositivo conocida y de bajo impacto si sale
/// mal. Todo lo demás —incluido lo que no se pudo clasificar— cae en <see cref="NeedsConfirmation"/>.
/// Fallar cerrado: un driver desconocido nunca se instala solo.
/// </remarks>
public enum DriverRiskClass
{
    /// <summary>
    /// Red, GPU, audio, USB, Bluetooth, impresora, entrada (teclado/mouse/cámara). Si sale mal, se
    /// revierte desde el Administrador de dispositivos o el punto de restauración sin dejar el
    /// equipo inutilizable. Se instala solo.
    /// </summary>
    Safe,

    /// <summary>
    /// Controlador de almacenamiento (SCSI/RAID/HDC) u otra clase que no matcheó la lista blanca.
    /// Si sale mal acá, se compromete el acceso al disco. Se detecta y se lista; nunca se instala
    /// sin que el técnico lo marque a propósito.
    /// </summary>
    NeedsConfirmation,

    /// <summary>
    /// Firmware o BIOS. No es un driver en el sentido de "programa que corre" — es el código de la
    /// placa. Un flasheo fallido es daño físico, no "reinstalar". Nunca se ofrece instalar desde
    /// acá, ni siquiera con confirmación: se nombra y se manda a la página del fabricante, igual
    /// que el disco mecánico o la RAM insuficiente.
    /// </summary>
    Firmware,
}

/// <summary>Una actualización de driver que el catálogo de Windows Update tiene para este equipo.</summary>
/// <param name="UpdateId">
/// GUID de <c>IUpdate.Identity.UpdateID</c>. Es el único identificador estable para volver a
/// encontrar el mismo update en una búsqueda posterior — los objetos COM no sobreviven entre
/// llamadas.
/// </param>
/// <param name="Title">Tal como lo da Windows Update: <c>"Fabricante - Clase - versión"</c>.</param>
/// <param name="Description">Puede ser <c>null</c>; no todos los updates lo traen.</param>
/// <param name="RiskClass">La clasificación de <see cref="DriverRiskClassifier"/>.</param>
public sealed record DriverUpdate(
    string UpdateId,
    string Title,
    string? Description,
    DriverRiskClass RiskClass);
