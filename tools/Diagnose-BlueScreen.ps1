<#
.SYNOPSIS
    Diagnostica pantallazos azules: qué código de parada, cuándo, y si coincide con una actualización.

.DESCRIPTION
    SOLO LECTURA. No modifica nada, no instala nada, no borra nada.

    Un BSOD no se arregla limpiando el equipo. Se arregla identificando qué lo causa, y para eso hay
    exactamente tres fuentes de verdad:

      1. El código de parada (bugcheck) del registro de eventos. Dice la CATEGORÍA del problema.
      2. Los eventos WHEA. Si hay, el problema es HARDWARE y ninguna actualización lo explica.
      3. La fecha de los pantallazos contra la fecha de las actualizaciones. Confirma o descarta
         la hipótesis de "fue una actualización".

    Este script lee las tres y las cruza.

.NOTES
    Correr como administrador. PowerShell 5.1 (el de Windows) o superior.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Diagnose-BlueScreen.ps1
#>
[CmdletBinding()]
param(
    [int]    $DaysBack = 60,
    [string] $OutFile = (Join-Path $env:TEMP ("bsod-{0}-{1}.txt" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmm')))
)

$ErrorActionPreference = 'Continue'
$report = New-Object System.Text.StringBuilder

function Say { param([string] $Text = '', [string] $Color = 'Gray')
    [void] $report.AppendLine($Text)
    Write-Host $Text -ForegroundColor $Color
}

function Header { param([string] $Text)
    Say ''
    Say ('=' * 74) DarkGray
    Say "  $Text" Cyan
    Say ('=' * 74) DarkGray
    Say ''
}

$since = (Get-Date).AddDays(-$DaysBack)
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Say "DIAGNÓSTICO DE PANTALLAZO AZUL — $env:COMPUTERNAME — $(Get-Date -Format 'yyyy-MM-dd HH:mm')" White
Say "Ventana analizada: últimos $DaysBack días.  Administrador: $isAdmin"
if (-not $isAdmin) { Say '  ADVERTENCIA: sin elevar, varias secciones van a venir vacías.' Yellow }

#region Catálogo de códigos de parada ---------------------------------------------------------
# Qué significa cada uno en la práctica. La columna que importa es "primer sospechoso".
$bugchecks = @{
    '0x0000000a' = 'IRQL_NOT_LESS_OR_EQUAL — un driver accedió a memoria que no debía. Driver.'
    '0x0000001a' = 'MEMORY_MANAGEMENT — administración de memoria. RAM defectuosa, o driver.'
    '0x0000001e' = 'KMODE_EXCEPTION_NOT_HANDLED — excepción en modo kernel. Driver.'
    '0x00000024' = 'NTFS_FILE_SYSTEM — sistema de archivos NTFS. DISCO o corrupción del volumen.'
    '0x0000003b' = 'SYSTEM_SERVICE_EXCEPTION — muy frecuente tras actualizar drivers de video. Driver.'
    '0x00000050' = 'PAGE_FAULT_IN_NONPAGED_AREA — RAM defectuosa es la causa #1. Después, driver.'
    '0x0000007a' = 'KERNEL_DATA_INPAGE_ERROR — no pudo leer del disco. DISCO fallando.'
    '0x0000007e' = 'SYSTEM_THREAD_EXCEPTION_NOT_HANDLED — driver.'
    '0x0000007f' = 'UNEXPECTED_KERNEL_MODE_TRAP — hardware: RAM o CPU. A veces overclock.'
    '0x0000009f' = 'DRIVER_POWER_STATE_FAILURE — un driver no respondió al suspender/reanudar. Driver.'
    '0x000000c2' = 'BAD_POOL_CALLER — driver.'
    '0x000000c4' = 'DRIVER_VERIFIER_DETECTED_VIOLATION — driver, detectado por el verificador.'
    '0x000000d1' = 'DRIVER_IRQL_NOT_LESS_OR_EQUAL — driver, típicamente de RED.'
    '0x000000ef' = 'CRITICAL_PROCESS_DIED — un proceso crítico murió. Corrupción del sistema o malware.'
    '0x000000f4' = 'CRITICAL_OBJECT_TERMINATION — DISCO, casi siempre.'
    '0x00000109' = 'CRITICAL_STRUCTURE_CORRUPTION — RAM, o un driver pisando memoria del kernel.'
    '0x00000116' = 'VIDEO_TDR_ERROR — la placa de video no respondió. Driver de video, o la placa.'
    '0x00000124' = 'WHEA_UNCORRECTABLE_ERROR — HARDWARE, sin vuelta. CPU, RAM o placa. Ver sección WHEA.'
    '0x00000133' = 'DPC_WATCHDOG_VIOLATION — un driver monopolizó el CPU. Frecuente con drivers SATA/SSD viejos.'
    '0x0000013a' = 'KERNEL_MODE_HEAP_CORRUPTION — driver.'
    '0x00000139' = 'KERNEL_SECURITY_CHECK_FAILURE — corrupción de estructuras. RAM o driver.'
    '0x000001d8' = 'SOFT_RESTART_FATAL_ERROR'
    '0x000000be' = 'ATTEMPTED_WRITE_TO_READONLY_MEMORY — driver.'
    '0x000000f7' = 'DRIVER_OVERRAN_STACK_BUFFER — driver.'
}

function Explain { param([string] $Code)
    if (-not $Code) { return $null }
    $key = $Code.ToLowerInvariant()
    if ($bugchecks.ContainsKey($key)) { return $bugchecks[$key] }
    return "Código no catalogado. Buscar '$Code bugcheck' en la documentación de Microsoft."
}
#endregion

#region 1 — Los pantallazos -------------------------------------------------------------------
Header '1 · PANTALLAZOS REGISTRADOS'

$crashes = @()

# Event 1001 de WER-SystemErrorReporting: el mensaje trae el bugcheck completo.
try {
    $events = Get-WinEvent -FilterHashtable @{
        LogName = 'System'; Id = 1001; StartTime = $since
    } -ErrorAction Stop | Where-Object { $_.ProviderName -match 'WER-SystemErrorReporting|BugCheck' }

    foreach ($e in $events) {
        # "The bugcheck was: 0x0000007e (0xffffffffc0000005, ...)"
        $code = $null
        if ($e.Message -match '(0x[0-9a-fA-F]{8})') { $code = $Matches[1].ToLowerInvariant() }

        $crashes += [pscustomobject]@{
            When = $e.TimeCreated
            Code = $code
            Meaning = Explain $code
        }
    }
}
catch {
    Say "  No se pudieron leer los eventos 1001: $($_.Exception.Message)" DarkYellow
}

if ($crashes.Count -eq 0) {
    Say '  No hay eventos de bugcheck en la ventana analizada.' Green
    Say '  Si igual ves pantallazos, puede que el volcado esté deshabilitado. Verificalo con:' DarkGray
    Say '    wmic recoveros get DebugInfoType' DarkGray
} else {
    Say "  $($crashes.Count) pantallazo(s) en los últimos $DaysBack días:" Yellow
    Say ''
    foreach ($c in $crashes) {
        # Sin '??': ese operador es de PowerShell 7 y Windows trae 5.1.
        $shown = if ($c.Code) { $c.Code } else { 'código no extraído' }
        Say ("  {0:yyyy-MM-dd HH:mm}   {1}" -f $c.When, $shown) White
        if ($c.Meaning) { Say "                       $($c.Meaning)" DarkGray }
    }

    Say ''
    Say '  Resumen por código:' Cyan
    $crashes | Where-Object Code | Group-Object Code | Sort-Object Count -Descending | ForEach-Object {
        Say ("    {0}  ×{1}" -f $_.Name, $_.Count) White
        Say ("      {0}" -f (Explain $_.Name)) DarkGray
    }
}

# Los archivos de volcado: confirman la frecuencia y sirven para un análisis profundo.
$minidumpDir = Join-Path $env:SystemRoot 'Minidump'
Say ''
if (Test-Path $minidumpDir) {
    $dumps = Get-ChildItem $minidumpDir -Filter *.dmp -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending
    if ($dumps) {
        Say "  Volcados en $minidumpDir : $($dumps.Count)" Yellow
        $dumps | Select-Object -First 8 | ForEach-Object {
            Say ("    {0:yyyy-MM-dd HH:mm}   {1}   {2:N0} KB" -f $_.LastWriteTime, $_.Name, ($_.Length / 1KB))
        }
    } else {
        Say "  La carpeta $minidumpDir existe pero está vacía." DarkGray
    }
} else {
    Say "  No existe $minidumpDir : los volcados pueden estar deshabilitados." DarkYellow
}
#endregion

#region 2 — WHEA: si hay algo acá, es hardware ------------------------------------------------
Header '2 · ERRORES DE HARDWARE (WHEA)'

Say '  WHEA es el mecanismo por el que el propio hardware le informa a Windows que falló.' DarkGray
Say '  Si hay eventos acá, el problema NO es una actualización.' DarkGray
Say ''

try {
    $whea = Get-WinEvent -FilterHashtable @{
        ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since
    } -ErrorAction Stop

    if ($whea) {
        Say "  !! $($whea.Count) evento(s) WHEA. El hardware está reportando fallas." Red
        $whea | Select-Object -First 6 | ForEach-Object {
            Say ("    {0:yyyy-MM-dd HH:mm}  ID {1}  {2}" -f $_.TimeCreated, $_.Id,
                 ($_.Message -split "`n")[0].Trim())
        }
        Say ''
        Say '  Traducción: CPU, RAM, placa o un dispositivo PCIe. Ninguna actualización arregla esto.' Red
    }
}
catch {
    Say '  Sin eventos WHEA. Descarta (no confirma) una falla de hardware detectada por el firmware.' Green
}
#endregion

#region 3 — Apagones inesperados --------------------------------------------------------------
Header '3 · APAGONES INESPERADOS (Kernel-Power 41)'

try {
    $power = Get-WinEvent -FilterHashtable @{
        ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41; StartTime = $since
    } -ErrorAction Stop

    if ($power) {
        Say "  $($power.Count) apagón(es) sin cierre limpio:" Yellow
        $power | Select-Object -First 8 | ForEach-Object {
            Say ("    {0:yyyy-MM-dd HH:mm}" -f $_.TimeCreated)
        }
        Say ''
        Say '  Si hay MÁS de estos que pantallazos, el equipo se está apagando sin BSOD:' DarkGray
        Say '  eso apunta a fuente de alimentación, sobrecalentamiento o RAM, no a software.' DarkGray
    }
}
catch { Say '  Ninguno.' Green }
#endregion

#region 4 — Errores de disco ------------------------------------------------------------------
Header '4 · ERRORES DE DISCO'

$diskErrors = @()
foreach ($provider in @('disk', 'Ntfs', 'volmgr', 'storahci')) {
    try {
        $found = Get-WinEvent -FilterHashtable @{
            LogName = 'System'; ProviderName = $provider; Level = 1, 2, 3; StartTime = $since
        } -ErrorAction Stop
        if ($found) { $diskErrors += $found }
    }
    catch { }
}

if ($diskErrors) {
    Say "  !! $($diskErrors.Count) error(es) de disco. Un disco con errores causa BSOD por sí solo." Red
    $diskErrors | Sort-Object TimeCreated -Descending | Select-Object -First 8 | ForEach-Object {
        Say ("    {0:yyyy-MM-dd HH:mm}  {1} ID {2}  {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id,
             ($_.Message -split "`n")[0].Trim())
    }
} else {
    Say '  Sin errores de disco registrados.' Green
}

Say ''
Say '  Estado SMART:' Cyan
try {
    Get-PhysicalDisk | ForEach-Object {
        $color = if ($_.HealthStatus -eq 'Healthy') { 'Green' } else { 'Red' }
        Say ("    {0}  {1}  {2}  {3}" -f $_.DeviceId, $_.MediaType, $_.HealthStatus, $_.FriendlyName) $color
    }
}
catch { Say '    No se pudo consultar.' DarkYellow }
#endregion

#region 5 — LA CORRELACIÓN: ¿fue una actualización? -------------------------------------------
Header '5 · ¿COINCIDEN LOS PANTALLAZOS CON UNA ACTUALIZACIÓN?'

$updates = @()
try {
    $updates = Get-HotFix -ErrorAction Stop |
               Where-Object { $_.InstalledOn -and $_.InstalledOn -ge $since } |
               Sort-Object InstalledOn -Descending |
               Select-Object HotFixID, InstalledOn, Description
}
catch { }

if ($updates) {
    Say "  Actualizaciones instaladas en la ventana:" Cyan
    $updates | ForEach-Object {
        Say ("    {0:yyyy-MM-dd}   {1,-12} {2}" -f $_.InstalledOn, $_.HotFixID, $_.Description)
    }
} else {
    Say '  No hay actualizaciones registradas en la ventana (o Get-HotFix no las lista todas:' DarkGray
    Say '  las actualizaciones acumulativas modernas no siempre aparecen acá).' DarkGray
}

Say ''
if ($crashes.Count -gt 0 -and $updates) {
    Say '  Cruce — para cada pantallazo, qué se instaló en los 7 días anteriores:' Cyan
    Say ''
    $anyCorrelation = $false
    foreach ($c in $crashes) {
        $before = $updates | Where-Object {
            $_.InstalledOn -le $c.When -and $_.InstalledOn -ge $c.When.AddDays(-7)
        }
        if ($before) {
            $anyCorrelation = $true
            Say ("    Pantallazo {0:yyyy-MM-dd HH:mm}" -f $c.When) Yellow
            $before | ForEach-Object {
                Say ("      <- {0:yyyy-MM-dd}  {1}" -f $_.InstalledOn, $_.HotFixID)
            }
        }
    }

    if (-not $anyCorrelation) {
        Say '    Ningún pantallazo cae dentro de los 7 días de una actualización.' Green
        Say '    La hipótesis de "fue una actualización" NO se sostiene con estos datos.' Green
    } else {
        Say ''
        Say '    OJO: coincidir en el tiempo no es causar. Si el código de parada apunta a RAM' DarkYellow
        Say '    o disco, la actualización es solo lo que destapó el problema de hardware.' DarkYellow
    }
}

# El primer pantallazo es el dato más informativo: qué cambió justo antes.
if ($crashes.Count -gt 0) {
    $first = ($crashes | Sort-Object When)[0]
    Say ''
    Say ("  El pantallazo MÁS ANTIGUO de la ventana es del {0:yyyy-MM-dd HH:mm}." -f $first.When) Cyan
    Say '  Preguntale al cliente qué cambió en esa fecha: una actualización, un programa nuevo,' DarkGray
    Say '  un pendrive, una impresora, un golpe al equipo. Ahí suele estar la respuesta.' DarkGray
}
#endregion

#region 6 — Drivers y dispositivos con problema -----------------------------------------------
Header '6 · DRIVERS Y DISPOSITIVOS'

try {
    $bad = Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0' -ErrorAction Stop
    if ($bad) {
        Say "  !! $($bad.Count) dispositivo(s) con problema:" Red
        $bad | ForEach-Object { Say ("    [{0}] {1}" -f $_.ConfigManagerErrorCode, $_.Name) }
    } else {
        Say '  Ningún dispositivo con problema en el Administrador de dispositivos.' Green
    }
}
catch { Say '  No se pudo consultar.' DarkYellow }

Say ''
Say '  Drivers de terceros instalados más recientemente (los sospechosos habituales):' Cyan
try {
    Get-CimInstance Win32_PnPSignedDriver -ErrorAction Stop |
        Where-Object { $_.DriverProviderName -and $_.DriverProviderName -ne 'Microsoft' -and $_.DriverDate } |
        Sort-Object DriverDate -Descending |
        Select-Object -First 10 |
        ForEach-Object {
            Say ("    {0:yyyy-MM-dd}  {1,-26} {2}" -f $_.DriverDate, $_.DriverProviderName, $_.DeviceName)
        }
}
catch { Say '    No se pudo consultar.' DarkYellow }
#endregion

#region 7 — Qué hacer -------------------------------------------------------------------------
Header '7 · QUÉ HACER, EN ESTE ORDEN'

Say '  1. Si hay eventos WHEA o SMART no está en Healthy: es HARDWARE. Respaldá y reemplazá.' White
Say '     Ninguna reparación de software sirve, y seguir usándolo puede perder datos.' DarkGray
Say ''
Say '  2. Si el código apunta a RAM (0x50, 0x1a, 0x109, 0x7f, 0x139): probá la memoria.' White
Say '     mdsched.exe   ->  "Reiniciar ahora y comprobar" (tarda entre 20 min y 2 h)' DarkGray
Say '     Mejor: MemTest86 desde USB, al menos 4 pasadas. Y si hay dos módulos, probá de a uno.' DarkGray
Say ''
Say '  3. Si el código apunta a DISCO (0x7a, 0xf4, 0x24): revisá el disco.' White
Say '     chkdsk C: /scan          (en línea, sin reiniciar)' DarkGray
Say ''
Say '  4. Si el código apunta a DRIVER y coincide con una actualización: revertí ESA actualización.' White
Say '     Ver las instaladas:   wmic qfe list brief /format:table' DarkGray
Say '     Desinstalar una:      wusa /uninstall /kb:XXXXXXX' DarkGray
Say '     O desde:              Configuración > Windows Update > Historial > Desinstalar actualizaciones' DarkGray
Say '     Si es un driver puntual: Administrador de dispositivos > Propiedades > Revertir al anterior.' DarkGray
Say ''
Say '  5. Verificá los archivos del sistema:' White
Say '     DISM /Online /Cleanup-Image /RestoreHealth' DarkGray
Say '     sfc /scannow' DarkGray
Say ''
Say '  6. Si nada es concluyente, hacé que el volcado diga el driver culpable:' White
Say '     - Instalá "WinDbg" desde la Microsoft Store (es gratis).' DarkGray
Say '     - Abrí el .dmp más reciente de C:\Windows\Minidump' DarkGray
Say '     - Escribí:  !analyze -v' DarkGray
Say '     La línea que importa es IMAGE_NAME o MODULE_NAME: ese es el archivo que lo está causando.' DarkGray
Say ''
Say '  7. Último recurso, y solo si sospechás un driver sin poder identificarlo:' White
Say '     verifier.exe  activa el Verificador de controladores. Fuerza el BSOD y nombra al culpable.' DarkGray
Say '     CUIDADO: puede dejar el equipo sin arrancar. Antes de activarlo, creá un punto de' Yellow
Say '     restauración y asegurate de saber entrar en Modo seguro (F8 / Shift+Reiniciar).' Yellow

Say ''
Say '  Lo que NO sirve para un pantallazo azul:' DarkYellow
Say '     limpiar temporales, limpiadores de registro, "optimizadores de RAM", desfragmentar.' DarkGray
Say '     Si alguien te dice que su programa arregla BSODs limpiando el equipo, está vendiendo humo.' DarkGray
#endregion

$report.ToString() | Set-Content -LiteralPath $OutFile -Encoding UTF8
Say ''
Say "Reporte guardado en: $OutFile" Green
