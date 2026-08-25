<#
.SYNOPSIS
    Arma la solución de EasyFix dentro de la VM Windows.

.DESCRIPTION
    Los .csproj están versionados, pero el .sln no: un GUID mal escrito a mano rompe el build de
    forma confusa. Este script lo genera con `dotnet new sln`, que siempre produce GUIDs válidos.

    Correr una sola vez, después de clonar el repo en la VM.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\bootstrap.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Raíz del repo: $root" -ForegroundColor Cyan

# --- Comprobaciones previas -----------------------------------------------------------------

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "No se encontró el SDK de .NET. Instalá .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
}

$sdks = & dotnet --list-sdks
Write-Host "SDKs instalados:" -ForegroundColor Cyan
$sdks | ForEach-Object { Write-Host "  $_" }
if (-not ($sdks -match '^8\.')) {
    Write-Warning "No se ve un SDK 8.x. El build va a fallar si no está."
}

if (-not $IsWindows -and $PSVersionTable.PSEdition -ne 'Desktop') {
    throw "Esto solo corre en Windows: WPF no compila en macOS ni Linux."
}

# --- Solución -------------------------------------------------------------------------------

$sln = Join-Path $root 'EasyFix.sln'
if (Test-Path $sln) {
    Write-Host "EasyFix.sln ya existe, se reutiliza." -ForegroundColor Yellow
} else {
    & dotnet new sln --name EasyFix
    Write-Host "EasyFix.sln creado." -ForegroundColor Green
}

$projects = @(
    'src\EasyFix.Core\EasyFix.Core.csproj',
    'src\EasyFix.App\EasyFix.App.csproj',
    'tests\EasyFix.Core.Tests\EasyFix.Core.Tests.csproj'
)

foreach ($p in $projects) {
    if (-not (Test-Path $p)) { throw "Falta el proyecto: $p" }
    & dotnet sln EasyFix.sln add $p
}

# --- Sysinternals ---------------------------------------------------------------------------
# autorunsc enumera TODAS las ubicaciones de autoarranque, con verificación de firma por entrada.
# Reimplementarlo a mano garantiza puntos ciegos. No se versiona: se descarga.

$autoruns = Join-Path $root 'tools\autorunsc.exe'
if (Test-Path $autoruns) {
    Write-Host "autorunsc.exe ya está presente." -ForegroundColor Yellow
} else {
    Write-Host "Descargando Sysinternals Autoruns..." -ForegroundColor Cyan
    $zip = Join-Path $env:TEMP 'Autoruns.zip'
    try {
        Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/Autoruns.zip' -OutFile $zip -UseBasicParsing
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $env:TEMP 'Autoruns') -Force
        Copy-Item (Join-Path $env:TEMP 'Autoruns\autorunsc.exe')   $autoruns -Force
        Copy-Item (Join-Path $env:TEMP 'Autoruns\autorunsc64.exe') (Join-Path $root 'tools\autorunsc64.exe') -Force
        Write-Host "autorunsc descargado en tools\." -ForegroundColor Green
    }
    catch {
        Write-Warning "No se pudo descargar Autoruns: $($_.Exception.Message)"
        Write-Warning "Bajalo a mano de https://learn.microsoft.com/sysinternals/downloads/autoruns y ponelo en tools\"
    }
    finally {
        Remove-Item $zip -ErrorAction SilentlyContinue
    }
}

# --- Restore y build ------------------------------------------------------------------------

Write-Host "`nRestaurando paquetes..." -ForegroundColor Cyan
& dotnet restore
if ($LASTEXITCODE -ne 0) { throw "dotnet restore falló." }

Write-Host "`nCompilando..." -ForegroundColor Cyan
& dotnet build -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build falló." }

Write-Host @"

Listo.

Siguiente paso — fase 1 del plan, el spike de contratos:

    powershell -ExecutionPolicy Bypass -File .\tools\spike\Verify-WindowsApis.ps1

Corrélo elevado, en Windows 10 Y en Windows 11, y diffeá las dos salidas antes de
escribir el código de diagnóstico.
"@ -ForegroundColor Green
