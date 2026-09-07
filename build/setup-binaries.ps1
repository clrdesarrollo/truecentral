<#
.SYNOPSIS
    Prepara los binarios de terceros que CLR TrueCentral VMS necesita en runtime
    y que no viven en el repositorio (cientos de MB de redistribuibles).

.DESCRIPTION
    Deja listas dos carpetas:

      native\hikvision  DLLs de HCNetSDK (el servidor las carga por P/Invoke)
      native\dahua      DLLs del NetSDK de Dahua
      tools\postgres    PostgreSQL portable (base embebida del servidor)
      tools\mediamtx    MediaMTX (media server que hace el fan-out RTSP)
      tools\ffmpeg-flyleaf  FFmpeg compartido que exige FlyleafLib (cliente WPF)
      tools\ffmpeg         FFmpeg y ffprobe de línea de comandos (exportación de
                           tramos en el servidor y proyección de pantalla al muro)

    Las DLLs nativas salen de Resources\ (los SDK tal como los entrega el
    fabricante). PostgreSQL y MediaMTX se copian desde otra instalación del
    producto si se indica -FromProduct.

.EXAMPLE
    .\build\setup-binaries.ps1
    Copia las DLLs nativas desde Resources\ y avisa qué falta.

.EXAMPLE
    .\build\setup-binaries.ps1 -FromProduct "C:\ruta\a\otro\checkout"
    Además copia tools\ (postgres, mediamtx, ffmpeg) desde ese directorio.
#>
[CmdletBinding()]
param(
    # Instalación existente del producto desde donde copiar tools\.
    [string] $FromProduct
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Copy-Tree {
    param([string] $Source, [string] $Destination, [string] $Label)

    if (-not (Test-Path $Source)) {
        Write-Warning "$Label`: no se encontró '$Source' (omitido)."
        return $false
    }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    # robocopy usa códigos 0-7 para éxito; 8+ es error real.
    robocopy $Source $Destination /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "$Label`: robocopy falló con código $LASTEXITCODE."
    }
    $mb = [math]::Round((Get-ChildItem $Destination -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "  OK  $Label -> $Destination ($mb MB)" -ForegroundColor Green
    return $true
}

Write-Host "CLR TrueCentral VMS - preparación de binarios" -ForegroundColor Cyan
Write-Host "Repositorio: $repo"
Write-Host ""

# ---------------------------------------------------------------------------
# 1. DLLs nativas de los SDK (desde Resources\)
# ---------------------------------------------------------------------------
Write-Host "[1/2] DLLs nativas de los SDK (Resources\ -> native\)"

$resources = Join-Path $repo 'Resources'
if (-not (Test-Path $resources)) {
    Write-Warning "No existe '$resources'. Descomprima ahí los SDK de Hikvision y Dahua (Win64) y vuelva a ejecutar."
} else {
    # Hikvision: carpeta lib\ del paquete EN-HCNetSDK*_win64
    $hikLib = Get-ChildItem $resources -Recurse -Directory -Filter 'lib' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'HCNetSDK.dll') } |
        Select-Object -First 1
    if ($hikLib) {
        Copy-Tree -Source $hikLib.FullName -Destination (Join-Path $repo 'native\hikvision') -Label 'Hikvision HCNetSDK' | Out-Null
    } else {
        Write-Warning "Hikvision: no se encontró una carpeta lib\ con HCNetSDK.dll dentro de Resources\."
    }

    # Dahua: carpeta Bin\ del paquete General_NetSDK_*_Win64 (sin las demos)
    $dahuaBin = Get-ChildItem $resources -Recurse -Directory -Filter 'Bin' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'dhnetsdk.dll') } |
        Select-Object -First 1
    if ($dahuaBin) {
        $dest = Join-Path $repo 'native\dahua'
        New-Item -ItemType Directory -Force $dest | Out-Null
        robocopy $dahuaBin.FullName $dest /E /XD Demo /XF *Demo*.exe /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Dahua: robocopy falló con código $LASTEXITCODE." }
        $mb = [math]::Round((Get-ChildItem $dest -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
        Write-Host "  OK  Dahua NetSDK -> $dest ($mb MB)" -ForegroundColor Green
    } else {
        Write-Warning "Dahua: no se encontró una carpeta Bin\ con dhnetsdk.dll dentro de Resources\."
    }
}

# ---------------------------------------------------------------------------
# 2. Herramientas redistribuibles (tools\)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "[2/2] Herramientas redistribuibles (tools\)"

if ($FromProduct) {
    foreach ($tool in 'postgres', 'mediamtx', 'ffmpeg', 'ffmpeg-flyleaf') {
        Copy-Tree -Source (Join-Path $FromProduct "tools\$tool") `
                  -Destination (Join-Path $repo "tools\$tool") -Label $tool | Out-Null
    }
} else {
    Write-Host "  (omitido: use -FromProduct '<ruta a otro checkout>' para copiarlas)" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 2b. Runtime de Visual C++ junto a PostgreSQL (despliegue "app-local")
#
# Los binarios de PostgreSQL para Windows (EDB) estan compilados con MSVC y
# dependen de VCRUNTIME140/MSVCP140, que NO vienen con Windows. En un servidor
# limpio, sin el redistribuible instalado, initdb.exe ni siquiera arranca: muere
# con 0xC0000135 (DLL no encontrada) y el servicio no puede crear la base.
# Copiarlas al lado de los .exe (la carpeta del ejecutable va primero en el
# orden de busqueda de DLL) evita depender de lo que tenga instalado el equipo
# destino y no cambia nada a nivel de maquina.
# ---------------------------------------------------------------------------
$crtNames = @('VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll')
$pgBin    = Join-Path $repo 'tools\postgres\pgsql\bin'

if (Test-Path $pgBin) {
    # Preferir el redistribuible de Visual Studio si existe; si no, System32
    # (son los mismos archivos que instala vc_redist.x64.exe).
    $crtSources = @()
    foreach ($vsRoot in @("${env:ProgramFiles}\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) {
        if (Test-Path $vsRoot) {
            $crtSources += Get-ChildItem -Path $vsRoot -Filter 'Microsoft.VC*.CRT' -Recurse -Directory -ErrorAction SilentlyContinue |
                           Where-Object { $_.FullName -like '*\x64\*' } |
                           Sort-Object FullName -Descending |
                           Select-Object -ExpandProperty FullName
        }
    }
    $crtSources += (Join-Path $env:SystemRoot 'System32')

    $copied = 0
    foreach ($name in $crtNames) {
        $src = $crtSources | ForEach-Object { Join-Path $_ $name } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $pgBin $name) -Force
            $copied++
        } else {
            Write-Host ("  [FALTA]  {0} (runtime de Visual C++ para PostgreSQL)" -f $name) -ForegroundColor Yellow
        }
    }
    if ($copied -gt 0) {
        Write-Host ("  [OK]     runtime de Visual C++ junto a PostgreSQL ({0} archivos)" -f $copied) -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# Resumen
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Estado:" -ForegroundColor Cyan
$checks = [ordered]@{
    'native\hikvision\HCNetSDK.dll'          = 'Driver Hikvision'
    'native\dahua\dhnetsdk.dll'              = 'Driver Dahua'
    'tools\postgres\pgsql\bin\pg_ctl.exe'    = 'PostgreSQL embebido (servidor)'
    'tools\postgres\pgsql\bin\VCRUNTIME140.dll' = 'Runtime de Visual C++ para PostgreSQL'
    'tools\postgres\pgsql\bin\MSVCP140.dll'     = 'Runtime de Visual C++ para PostgreSQL (C++)'
    'tools\mediamtx\mediamtx.exe'            = 'MediaMTX (streaming)'
    'tools\iprp\HikIpReceiverPro-Setup.exe'  = 'Hik IP Receiver Pro (receptor de paneles, opcional)'
    'tools\ffmpeg-flyleaf\avcodec-63.dll'    = 'FFmpeg (cliente WPF)'
    'tools\ffmpeg\bin\ffmpeg.exe'           = 'FFmpeg CLI (exportación y proyección al muro)'
}
$missing = 0
foreach ($path in $checks.Keys) {
    $full = Join-Path $repo $path
    if (Test-Path $full) {
        Write-Host ("  [OK]     {0}" -f $checks[$path]) -ForegroundColor Green
    } else {
        Write-Host ("  [FALTA]  {0}  ({1})" -f $checks[$path], $path) -ForegroundColor Yellow
        $missing++
    }
}

if ($missing -gt 0) {
    Write-Host ""
    Write-Host "Faltan $missing componente(s). Dónde obtenerlos:" -ForegroundColor Yellow
    Write-Host "  - SDK Hikvision/Dahua : portal de desarrolladores del fabricante (versión Win64) -> Resources\"
    Write-Host "  - PostgreSQL portable : https://www.enterprisedb.com/download-postgresql-binaries -> tools\postgres\pgsql"
    Write-Host "  - MediaMTX v1.20      : https://github.com/bluenviron/mediamtx/releases -> tools\mediamtx"
    Write-Host "  - Hik IP Receiver Pro : instalador oficial de Hikvision (V2.5.0 o superior; el API de alta de equipos existe desde la 2.5.0)"
    Write-Host "                          renombrado a tools\iprp\HikIpReceiverPro-Setup.exe (opcional: sin el, la suite se compila sin el receptor)"
    Write-Host "  - FFmpeg para Flyleaf : asset del release de FlyleafLib con la MISMA versión que el paquete NuGet"
    Write-Host "                          https://github.com/SuRGeoNix/Flyleaf/releases -> carpeta FFmpeg\ -> tools\ffmpeg-flyleaf"
    Write-Host "  - FFmpeg CLI          : build de Windows (gyan.dev / BtbN) -> tools\ffmpeg (con bin\ffmpeg.exe y bin\ffprobe.exe)"
} else {
    Write-Host ""
    Write-Host "Todo listo. Compile con: dotnet build CLRTrueCentralVMS.slnx" -ForegroundColor Green
}
