<#
.SYNOPSIS
    EasyFix — Fase 1: spike de verificación de contratos de Windows.

.DESCRIPTION
    SOLO LECTURA. No modifica nada del sistema.

    Todo el plan de EasyFix se apoya en contratos de WMI, registro, Event Log y contadores de
    rendimiento citados de memoria. Este script los consulta en el equipo real e imprime los valores
    verdaderos, para no construir código sobre suposiciones.

    Correr en Windows 10 Y en Windows 11 y comparar las dos salidas: varios de estos contratos
    difieren entre versiones.

.NOTES
    Requiere PowerShell 5.1 (el que viene con Windows 10/11). Compatible con PowerShell 7.
    Requiere ejecutarse como administrador — varias clases WMI no son legibles de otro modo.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Verify-WindowsApis.ps1
    powershell -ExecutionPolicy Bypass -File .\Verify-WindowsApis.ps1 -OutFile C:\temp\win11.md
#>
[CmdletBinding()]
param(
    [string] $OutFile = (Join-Path $env:TEMP ("easyfix-spike-{0}-{1}.md" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Continue'
$script:Report = New-Object System.Text.StringBuilder
$script:Results = @()

#region Infraestructura de salida ------------------------------------------------------------

function Write-Line { param([string] $Text = '')
    [void] $script:Report.AppendLine($Text)
    Write-Host $Text
}

function Write-Section { param([string] $Title)
    Write-Line ''
    Write-Line "## $Title"
    Write-Line ''
}

# Envuelve cada verificación: nunca deja que una falla corte el resto del spike.
function Test-Contract {
    param(
        [Parameter(Mandatory)] [string]    $Name,
        [Parameter(Mandatory)] [string]    $Expected,
        [Parameter(Mandatory)] [scriptblock] $Probe
    )

    Write-Line "### $Name"
    Write-Line ''
    Write-Line "**Esperado (del plan):** $Expected"
    Write-Line ''

    $status = 'OK'
    $output = $null
    try {
        $output = & $Probe 2>&1 | Out-String
        if ([string]::IsNullOrWhiteSpace($output)) {
            $status = 'VACIO'
            $output = '(sin resultados — el contrato existe pero no devolvió datos en este equipo)'
        }
    }
    catch {
        $status = 'ERROR'
        $output = "$($_.Exception.GetType().Name): $($_.Exception.Message)"
    }

    Write-Line "**Real:** ``$status``"
    Write-Line ''
    Write-Line '```'
    Write-Line $output.TrimEnd()
    Write-Line '```'
    Write-Line ''

    $script:Results += [pscustomobject]@{ Name = $Name; Status = $status }
}

#endregion

#region Encabezado ---------------------------------------------------------------------------

$os      = Get-CimInstance Win32_OperatingSystem
$cs      = Get-CimInstance Win32_ComputerSystem
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Line "# EasyFix — Spike de contratos de Windows"
Write-Line ''
Write-Line "| | |"
Write-Line "|---|---|"
Write-Line "| Equipo | $env:COMPUTERNAME |"
Write-Line "| SO | $($os.Caption) build $($os.BuildNumber) ($($os.OSArchitecture)) |"
Write-Line "| Fabricante | $($cs.Manufacturer) $($cs.Model) |"
Write-Line "| PowerShell | $($PSVersionTable.PSVersion) |"
Write-Line "| Administrador | $isAdmin |"
Write-Line "| Idioma del SO | $((Get-Culture).Name) |"
Write-Line "| Fecha | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') |"

if (-not $isAdmin) {
    Write-Line ''
    Write-Line '> ⚠️ **No estás como administrador.** Varias secciones van a fallar. Reabrí la consola elevada.'
}

#endregion

#region 1 — Disco ----------------------------------------------------------------------------

Write-Section '1 · Disco físico'

Test-Contract -Name 'MSFT_PhysicalDisk — MediaType y BusType' `
    -Expected 'MediaType: 3=HDD, 4=SSD, 5=SCM, 0=desconocido · BusType: 17=NVMe, 11=SATA, 8=RAID · namespace root\Microsoft\Windows\Storage' `
    -Probe {
        Get-CimInstance -Namespace 'root\Microsoft\Windows\Storage' -ClassName MSFT_PhysicalDisk |
            Select-Object DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus,
                          @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}}, SpindleSpeed |
            Format-List
    }

# SpindleSpeed es el plan B cuando MediaType devuelve 0 (pasa con varios controladores RAID):
# 0 = SSD, 1 = indeterminado, >1 = RPM reales de un disco mecánico.
Test-Contract -Name 'Fallback de detección — Get-PhysicalDisk' `
    -Expected 'El cmdlet expone MediaType como texto (HDD/SSD/Unspecified), útil si el entero viene en 0' `
    -Probe { Get-PhysicalDisk | Select-Object DeviceId, MediaType, BusType, HealthStatus, SpindleSpeed | Format-Table -AutoSize }

Test-Contract -Name 'MSStorageDriver_FailurePredictStatus — SMART' `
    -Expected 'namespace root\wmi · propiedades PredictFailure (bool) y Reason (int)' `
    -Probe {
        Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_FailurePredictStatus |
            Select-Object InstanceName, PredictFailure, Reason, Active | Format-List
    }

Test-Contract -Name 'TRIM habilitado' `
    -Expected 'fsutil behavior query DisableDeleteNotify → 0 = TRIM activo. Win10+ imprime una línea por sistema de archivos (NTFS y ReFS)' `
    -Probe { & "$env:SystemRoot\System32\fsutil.exe" behavior query DisableDeleteNotify }

Test-Contract -Name 'Espacio libre por volumen' `
    -Expected 'Win32_LogicalDisk DriveType=3 para discos fijos' `
    -Probe {
        Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
            Select-Object DeviceID, @{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}},
                          @{n='TotalGB';e={[math]::Round($_.Size/1GB,1)}},
                          @{n='FreePct';e={if($_.Size){[math]::Round(100*$_.FreeSpace/$_.Size,1)}}} |
            Format-Table -AutoSize
    }

#endregion

#region 2 — Memoria --------------------------------------------------------------------------

Write-Section '2 · Memoria'

Test-Contract -Name 'Win32_PhysicalMemory — módulos instalados' `
    -Expected 'SMBIOSMemoryType: 24=DDR3, 26=DDR4, 34=DDR5. Speed en MHz. Necesario para recomendar el módulo exacto.' `
    -Probe {
        Get-CimInstance Win32_PhysicalMemory |
            Select-Object DeviceLocator, BankLabel, @{n='GB';e={$_.Capacity/1GB}},
                          Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber |
            Format-List
    }

Test-Contract -Name 'Win32_PhysicalMemoryArray — slots totales' `
    -Expected 'MemoryDevices = slots físicos totales. Slots libres = MemoryDevices menos el conteo de Win32_PhysicalMemory.' `
    -Probe {
        $arr  = Get-CimInstance Win32_PhysicalMemoryArray
        $used = @(Get-CimInstance Win32_PhysicalMemory).Count
        "MemoryDevices (slots totales) : $($arr.MemoryDevices)"
        "Slots ocupados               : $used"
        "Slots libres                 : $($arr.MemoryDevices - $used)"
        "MaxCapacity (KB)             : $($arr.MaxCapacity)"
        "MaxCapacityEx (KB)           : $($arr.MaxCapacityEx)"
    }

# ⚠️ Los nombres de contador de rendimiento están LOCALIZADOS. En Windows en español,
# '\Memory\Committed Bytes' no existe: es '\Memoria\Bytes confirmados'. Por eso se prueban
# las dos vías y se documenta cuál usar desde C#.
Test-Contract -Name 'Presión de memoria — vía contadores (LOCALIZADA, frágil)' `
    -Expected 'Get-Counter con nombres en inglés. FALLA en Windows en español → hay que usar la vía CIM de abajo.' `
    -Probe {
        Get-Counter -Counter '\Memory\Committed Bytes','\Memory\Commit Limit','\Paging File(_Total)\% Usage' `
                    -MaxSamples 1 -ErrorAction Stop |
            Select-Object -ExpandProperty CounterSamples |
            Select-Object Path, CookedValue | Format-List
    }

Test-Contract -Name 'Presión de memoria — vía CIM (independiente del idioma, PREFERIDA)' `
    -Expected 'Win32_PerfFormattedData_PerfOS_Memory: nombres de propiedad en inglés siempre, sin importar el idioma del SO. Esta es la que va al código.' `
    -Probe {
        $m = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
        $pct = if ($m.CommitLimit) { [math]::Round(100.0 * $m.CommittedBytes / $m.CommitLimit, 1) } else { 'n/d' }
        "CommittedBytes   : $($m.CommittedBytes)"
        "CommitLimit      : $($m.CommitLimit)"
        "Commit %         : $pct   <-- umbral del plan: >85% = RAM insuficiente"
        "PagesPerSec      : $($m.PagesPerSec)"
        "AvailableMBytes  : $($m.AvailableMBytes)"
    }

#endregion

#region 3 — CPU y disco: rendimiento --------------------------------------------------------

Write-Section '3 · CPU y latencia de disco'

Test-Contract -Name 'Win32_Processor' `
    -Expected 'CurrentClockSpeed muy por debajo de MaxClockSpeed y sostenido = throttling' `
    -Probe {
        Get-CimInstance Win32_Processor |
            Select-Object Name, NumberOfCores, NumberOfLogicalProcessors,
                          MaxClockSpeed, CurrentClockSpeed, LoadPercentage | Format-List
    }

Test-Contract -Name 'Throttling — % of Maximum Frequency (vía CIM)' `
    -Expected 'Win32_PerfFormattedData_Counters_ProcessorInformation.PercentofMaximumFrequency. <70% sostenido = throttle térmico.' `
    -Probe {
        Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'" |
            Select-Object Name, PercentofMaximumFrequency, PercentProcessorTime, ProcessorFrequency | Format-List
    }

Test-Contract -Name 'Latencia de disco (vía CIM)' `
    -Expected 'Win32_PerfFormattedData_PerfDisk_PhysicalDisk: AvgDisksecPerTransfer en ms. >25 ms sostenido = el disco es el cuello de botella.' `
    -Probe {
        Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" |
            Select-Object Name, AvgDisksecPerTransfer, AvgDisksecPerRead, AvgDisksecPerWrite,
                          CurrentDiskQueueLength, PercentIdleTime | Format-List
    }

Test-Contract -Name 'Desgaste de batería' `
    -Expected 'powercfg /batteryreport genera HTML/XML con DesignCapacity vs FullChargeCapacity. En desktop no existe batería → debe fallar limpio.' `
    -Probe {
        $bat = Get-CimInstance Win32_Battery
        if (-not $bat) { return 'Sin batería (desktop). El chequeo debe saltarse, no fallar.' }
        $xml = Join-Path $env:TEMP 'easyfix-battery.xml'
        & "$env:SystemRoot\System32\powercfg.exe" /batteryreport /output $xml /xml | Out-Null
        if (-not (Test-Path $xml)) { throw 'powercfg no generó el XML' }
        $x = [xml] (Get-Content $xml -Raw)
        $b = $x.BatteryReport.Batteries.Battery | Select-Object -First 1
        "DesignCapacity     : $($b.DesignCapacity)"
        "FullChargeCapacity : $($b.FullChargeCapacity)"
        if ($b.DesignCapacity -and [double]$b.DesignCapacity -gt 0) {
            $wear = 100 - (100.0 * [double]$b.FullChargeCapacity / [double]$b.DesignCapacity)
            "Desgaste           : $([math]::Round($wear,1))%   <-- umbral del plan: >30%"
        }
        "(XML completo en $xml — borralo, es el único archivo que este spike escribe)"
    }

#endregion

#region 4 — Arranque -------------------------------------------------------------------------

Write-Section '4 · Arranque'

Test-Contract -Name 'Event 100 — tiempo de boot (LA métrica antes/después)' `
    -Expected 'Log Microsoft-Windows-Diagnostics-Performance/Operational, ID 100, campos BootTime / MainPathBootTime / BootPostBootTime en ms' `
    -Probe {
        $ev = Get-WinEvent -FilterHashtable @{
                  LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 100
              } -MaxEvents 3 -ErrorAction Stop

        foreach ($e in $ev) {
            $x = [xml] $e.ToXml()
            "--- $($e.TimeCreated) ---"
            # Se imprimen TODOS los nombres de campo: es el punto del spike.
            $x.Event.EventData.Data | ForEach-Object { "  {0,-26} = {1}" -f $_.Name, $_.'#text' }
        }
    }

Test-Contract -Name 'Events 101/103 — costo por app en ms' `
    -Expected 'ID 101 = app tardó más de lo normal; 103 = servicio. Campos Name, TotalTime, DegradationTime. Es de donde el Task Manager saca "Impacto de inicio".' `
    -Probe {
        $ev = Get-WinEvent -FilterHashtable @{
                  LogName = 'Microsoft-Windows-Diagnostics-Performance/Operational'; Id = 101,103
              } -MaxEvents 5 -ErrorAction Stop

        foreach ($e in $ev) {
            $x = [xml] $e.ToXml()
            "--- ID $($e.Id) · $($e.TimeCreated) ---"
            $x.Event.EventData.Data | ForEach-Object { "  {0,-26} = {1}" -f $_.Name, $_.'#text' }
        }
    }

Test-Contract -Name 'StartupApproved — el byte de habilitado/deshabilitado' `
    -Expected 'HKCU\...\Explorer\StartupApproved\Run · byte[0]: 02/06 = habilitado, 03 = deshabilitado. Los bytes 4-11 son un FILETIME con la fecha de desactivación.' `
    -Probe {
        $roots = @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run',
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32',
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
        )
        foreach ($r in $roots) {
            "=== $r ==="
            if (-not (Test-Path $r)) { '  (no existe)'; continue }
            $k = Get-Item $r
            if (-not $k.Property) { '  (sin valores)'; continue }
            foreach ($p in $k.Property) {
                $bytes = (Get-ItemProperty -Path $r -Name $p).$p
                $hex   = ($bytes | ForEach-Object { $_.ToString('X2') }) -join ' '
                $state = switch ($bytes[0]) { 2 {'HABILITADO'} 6 {'HABILITADO'} 3 {'DESHABILITADO'} default {"? (0x{0:X2})" -f $bytes[0]} }
                "  {0,-34} len={1,-3} {2,-12} [{3}]" -f $p, $bytes.Count, $state, $hex
            }
        }
    }

Test-Contract -Name 'Entradas de inicio — las 5 ubicaciones' `
    -Expected 'Run de HKCU/HKLM (+Wow6432Node), carpetas Startup de usuario y de todos los usuarios, tareas programadas con trigger de logon' `
    -Probe {
        foreach ($r in @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run')) {
            "=== $r ==="
            if (Test-Path $r) {
                (Get-Item $r).Property | ForEach-Object { "  {0,-30} = {1}" -f $_, (Get-ItemProperty $r -Name $_).$_ }
            } else { '  (no existe)' }
        }
        foreach ($f in @(
            [Environment]::GetFolderPath('Startup'),
            [Environment]::GetFolderPath('CommonStartup'))) {
            "=== $f ==="
            Get-ChildItem -LiteralPath $f -ErrorAction SilentlyContinue | ForEach-Object { "  $($_.Name)" }
        }
        "=== Tareas con trigger de logon ==="
        Get-ScheduledTask -ErrorAction SilentlyContinue |
            Where-Object { $_.State -ne 'Disabled' -and $_.Triggers.CimClass.CimClassName -contains 'MSFT_TaskLogonTrigger' } |
            Select-Object -First 15 | ForEach-Object { "  $($_.TaskPath)$($_.TaskName)" }
    }

Test-Contract -Name 'Fast Startup' `
    -Expected 'HiberbootEnabled en HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power · 1 = activo' `
    -Probe {
        Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' |
            Select-Object HiberbootEnabled, HibernateEnabled, HibernateEnabledDefault | Format-List
    }

#endregion

#region 5 — Restauración y BitLocker --------------------------------------------------------

Write-Section '5 · Punto de restauración y BitLocker'

Test-Contract -Name 'Clase SystemRestore — ¿existe el contrato?' `
    -Expected 'Clase SystemRestore en namespace root\default, con método CreateRestorePoint. SOLO SE INSPECCIONA — este spike no crea nada.' `
    -Probe {
        $c = Get-CimClass -Namespace 'root\default' -ClassName SystemRestore -ErrorAction Stop
        "Clase encontrada: $($c.CimClassName)"
        'Métodos:'
        $c.CimClassMethods | ForEach-Object { "  $($_.Name)" }
        ''
        'Puntos de restauración existentes:'
        $rp = Get-ComputerRestorePoint -ErrorAction SilentlyContinue
        if ($rp) { $rp | Select-Object SequenceNumber, Description, CreationTime | Format-Table -AutoSize | Out-String }
        else     { '  (ninguno — System Restore puede estar DESHABILITADO en este equipo)' }
    }

Test-Contract -Name '¿System Restore está habilitado?' `
    -Expected 'Si no hay shadow storage para C:, el plan manda ABORTAR antes de aplicar cualquier fix' `
    -Probe {
        & "$env:SystemRoot\System32\vssadmin.exe" list shadowstorage
        ''
        'Throttle de creación (minutos; 1440 = 24 h por defecto, 0 = sin límite):'
        $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
        if (Test-Path $k) {
            (Get-ItemProperty $k -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
        } else { '  (clave ausente → aplica el default de 1440)' }
    }

Test-Contract -Name 'BitLocker — el riesgo de dejar al cliente fuera de su equipo' `
    -Expected 'Win32_EncryptableVolume en root\CIMV2\Security\MicrosoftVolumeEncryption · ProtectionStatus: 0=off, 1=on, 2=desconocido' `
    -Probe {
        Get-CimInstance -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' `
                        -ClassName Win32_EncryptableVolume -ErrorAction Stop |
            Select-Object DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod, VolumeType |
            Format-Table -AutoSize
    }

Test-Contract -Name 'Equipo unido a dominio (activa el modo restringido)' `
    -Expected 'Win32_ComputerSystem.PartOfDomain = true → las GPO revierten los cambios' `
    -Probe {
        Get-CimInstance Win32_ComputerSystem |
            Select-Object PartOfDomain, Domain, Workgroup, DomainRole | Format-List
    }

#endregion

#region 6 — Energía --------------------------------------------------------------------------

Write-Section '6 · Plan de energía'

Test-Contract -Name 'GUIDs de esquemas de energía' `
    -Expected 'Equilibrado 381b4222-f694-41f0-9685-ff5bb260df2e · Alto rendimiento 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c · Ahorro a1841308-3541-4fab-bc81-f71556f20b4a. En equipos OEM los GUIDs pueden ser propios.' `
    -Probe {
        'Esquema activo:'
        & "$env:SystemRoot\System32\powercfg.exe" /getactivescheme
        ''
        'Todos los esquemas disponibles:'
        & "$env:SystemRoot\System32\powercfg.exe" /list
    }

#endregion

#region 7 — Servicios, seguridad, firmas ----------------------------------------------------

Write-Section '7 · Clasificador de 3 capas'

Test-Contract -Name 'Capa 1 — productos de seguridad registrados' `
    -Expected 'root\SecurityCenter2: AntiVirusProduct, FirewallProduct, AntiSpywareProduct. Dos AntiVirusProduct activos = conflicto.' `
    -Probe {
        foreach ($cls in 'AntiVirusProduct','FirewallProduct','AntiSpywareProduct') {
            "=== $cls ==="
            $r = Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName $cls -ErrorAction SilentlyContinue
            if ($r) { $r | Select-Object displayName, productState, pathToSignedProductExe | Format-List | Out-String }
            else    { '  (ninguno)' }
        }
    }

Test-Contract -Name 'Capa 1 — grafo de dependencias de servicios' `
    -Expected 'Win32_DependentService relaciona Antecedent (del que se depende) con Dependent. Nunca desactivar un servicio con dependientes en ejecución.' `
    -Probe {
        Get-CimInstance Win32_DependentService -ErrorAction Stop |
            Select-Object -First 10 @{n='Antecedent';e={$_.Antecedent.Name}},
                                    @{n='Dependent'; e={$_.Dependent.Name}} |
            Format-Table -AutoSize
        ''
        'Vía alternativa (más simple para el código):'
        Get-Service -Name 'RpcSs' | Select-Object Name, @{n='DependentServices';e={($_.DependentServices | Select-Object -First 5).Name -join ', '}} | Format-List
    }

Test-Contract -Name 'Capa 1/2 — firma Authenticode y publisher' `
    -Expected 'Get-AuthenticodeSignature.SignerCertificate.Subject contiene el O= del publisher. Es la base del clasificador: un cert se verifica, un nombre se falsifica.' `
    -Probe {
        $targets = @("$env:SystemRoot\System32\svchost.exe", "$env:SystemRoot\explorer.exe")
        foreach ($t in $targets) {
            $s = Get-AuthenticodeSignature -LiteralPath $t
            "=== $t ==="
            "  Status    : $($s.Status)"
            "  Subject   : $($s.SignerCertificate.Subject)"
            "  Issuer    : $($s.SignerCertificate.Issuer)"
        }
        ''
        'Un binario sin firmar debe dar Status = NotSigned (y en el plan cae en Capa 3):'
        $unsigned = Join-Path $env:TEMP 'easyfix-unsigned-probe.txt'
        'x' | Set-Content -LiteralPath $unsigned
        (Get-AuthenticodeSignature -LiteralPath $unsigned).Status
        Remove-Item -LiteralPath $unsigned -Force
    }

Test-Contract -Name 'Programas instalados — claves Uninstall (NUNCA Win32_Product)' `
    -Expected 'Win32_Product dispara reconfiguración MSI de cada paquete: tarda minutos y puede romper instalaciones. Se leen las claves de registro.' `
    -Probe {
        $keys = @(
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
        )
        $all = Get-ItemProperty $keys -ErrorAction SilentlyContinue |
               Where-Object { $_.DisplayName }
        "Total de entradas con DisplayName: $(@($all).Count)"
        ''
        'Muestra de 10 (DisplayName / Publisher / tamaño estimado):'
        $all | Select-Object -First 10 DisplayName, Publisher,
                    @{n='MB';e={if($_.EstimatedSize){[math]::Round($_.EstimatedSize/1024,1)}}} |
               Format-Table -AutoSize
    }

Test-Contract -Name 'Dispositivos con problema' `
    -Expected 'Win32_PnPEntity con ConfigManagerErrorCode distinto de 0 = driver roto o ausente' `
    -Probe {
        $bad = Get-CimInstance Win32_PnPEntity -Filter 'ConfigManagerErrorCode <> 0'
        if (-not $bad) { return 'Ningún dispositivo con problema (esperado en una VM limpia).' }
        $bad | Select-Object Name, DeviceID, ConfigManagerErrorCode, Status | Format-List
    }

#endregion

#region 8 — winget ---------------------------------------------------------------------------

Write-Section '8 · winget'

Test-Contract -Name '¿winget está presente?' `
    -Expected 'Viene en Win10 1809+ y Win11. Si falta, el plan hace bootstrap de Microsoft.DesktopAppInstaller.' `
    -Probe {
        $w = Get-Command winget.exe -ErrorAction SilentlyContinue
        if (-not $w) { return 'winget NO ENCONTRADO → hay que ejercitar la ruta de bootstrap.' }
        "Ruta    : $($w.Source)"
        "Versión : $(& winget.exe --version)"
    }

Test-Contract -Name 'Los IDs de paquete del plan — ¿existen de verdad?' `
    -Expected 'Google.Chrome · Adobe.Acrobat.Reader.64-bit · 7zip.7zip · Microsoft.VCRedist.2015+.x64 · VideoLAN.VLC · Notepad++.Notepad++ · AnyDesk.AnyDesk' `
    -Probe {
        if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { return 'Sin winget, no se puede verificar.' }
        $ids = @('Google.Chrome','Adobe.Acrobat.Reader.64-bit','7zip.7zip',
                 'Microsoft.VCRedist.2015+.x64','Microsoft.VCRedist.2015+.x86',
                 'VideoLAN.VLC','Notepad++.Notepad++','AnyDesk.AnyDesk','RARLab.WinRAR')
        foreach ($id in $ids) {
            $out  = & winget.exe show --id $id --exact --accept-source-agreements 2>&1 | Out-String
            $code = $LASTEXITCODE
            $mark = if ($code -eq 0) { 'EXISTE     ' } else { "NO RESUELVE" }
            "  {0} {1,-32} (exit {2})" -f $mark, $id, $code
        }
        ''
        'Exit codes de winget que el código debe distinguir:'
        '  0x0        = éxito'
        '  0x8A150061 = NO_APPLICABLE_INSTALLER'
        '  0x8A15002B = NO_APPLICATIONS_FOUND'
        '  0x8A150056 = PACKAGE_ALREADY_INSTALLED  <-- NO es un fallo'
    }

#endregion

#region 9 — Espacio recuperable --------------------------------------------------------------

Write-Section '9 · Espacio recuperable (rápido — sin DISM)'

Test-Contract -Name 'Tamaños de temporales, Windows.old y hiberfil' `
    -Expected 'Solo medición. DISM /AnalyzeComponentStore va en el escaneo profundo, no acá.' `
    -Probe {
        function Get-DirSize([string] $Path) {
            if (-not (Test-Path -LiteralPath $Path)) { return $null }
            $s = (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
                  Measure-Object -Property Length -Sum).Sum
            if ($s) { [math]::Round($s/1MB, 1) } else { 0 }
        }
        foreach ($p in @($env:TEMP, "$env:SystemRoot\Temp",
                         "$env:SystemRoot\SoftwareDistribution\Download",
                         "$env:SystemRoot\..\Windows.old")) {
            $mb = Get-DirSize $p
            if ($null -eq $mb) { "  {0,-58} (no existe)" -f $p }
            else               { "  {0,-58} {1,10} MB" -f $p, $mb }
        }
        $hib = "$env:SystemDrive\hiberfil.sys"
        if (Test-Path -LiteralPath $hib) {
            "  {0,-58} {1,10} MB" -f $hib, [math]::Round((Get-Item -LiteralPath $hib -Force).Length/1MB,1)
        } else { "  hiberfil.sys                                               (ausente)" }
        ''
        '¿Hay reparse points (junctions) dentro de %TEMP%? Si sí, el borrado recursivo ingenuo es peligroso:'
        $rp = Get-ChildItem -LiteralPath $env:TEMP -Recurse -Force -Directory -ErrorAction SilentlyContinue |
              Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
              Select-Object -First 10
        if ($rp) { $rp | ForEach-Object { "  !! $($_.FullName) -> $($_.Target)" } }
        else     { '  ninguno en este momento (probar igual el caso a mano en la VM)' }
    }

#endregion

#region Resumen ------------------------------------------------------------------------------

Write-Section 'Resumen'

$ok    = @($script:Results | Where-Object Status -eq 'OK').Count
$empty = @($script:Results | Where-Object Status -eq 'VACIO').Count
$err   = @($script:Results | Where-Object Status -eq 'ERROR').Count

Write-Line "| Resultado | Cantidad |"
Write-Line "|---|---|"
Write-Line "| OK | $ok |"
Write-Line "| Vacío | $empty |"
Write-Line "| Error | $err |"
Write-Line ''

if ($err -gt 0) {
    Write-Line '**Contratos que fallaron — corregir el plan antes de escribir el código correspondiente:**'
    Write-Line ''
    $script:Results | Where-Object Status -eq 'ERROR' | ForEach-Object { Write-Line "- $($_.Name)" }
    Write-Line ''
}

Write-Line '**Siguiente paso:** correr este mismo script en la otra versión de Windows (10 si esto fue 11, o al revés) y diffear las dos salidas. Las diferencias son casos condicionales que el código tiene que manejar.'

$script:Report.ToString() | Set-Content -LiteralPath $OutFile -Encoding UTF8
Write-Host ''
Write-Host "Reporte escrito en: $OutFile" -ForegroundColor Green

#endregion
