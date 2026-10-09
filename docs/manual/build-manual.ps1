<#
.SYNOPSIS
  Compila el manual de usuario (Typst) a PDF con la versión del archivo VERSION.

.EXAMPLE
  .\build-manual.ps1                          # dist\CLRTrueCentralVMS-Manual-<versión>.pdf
  .\build-manual.ps1 -Watch                   # recompila cada vez que se guarda un archivo
  .\build-manual.ps1 -Modulos video,playback  # edición con solo esos módulos
  .\build-manual.ps1 -Final                   # entrega: falla si queda algo pendiente
  .\build-manual.ps1 -RevisarReferencias      # compila cada edición de un módulo menos y
                                              # falla si alguna queda con referencias colgando
#>
param(
    [string[]]$Modulos = @(),
    [switch]$Final,
    [switch]$RevisarReferencias,
    [switch]$Watch,
    [string]$Salida
)
$ErrorActionPreference = 'Stop'

$raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$version = (Get-Content (Join-Path $raiz 'VERSION') -Raw).Trim()
if (-not $Salida) { $Salida = Join-Path $raiz "dist\CLRTrueCentralVMS-Manual-$version.pdf" }
New-Item -ItemType Directory -Force (Split-Path $Salida) | Out-Null

$typst = Get-Command typst -ErrorAction SilentlyContinue
if (-not $typst) { throw 'No se encontró typst. Instálelo con: winget install --id Typst.Typst -e' }

if ($RevisarReferencias) {
    # Claves de LicenseFeatures sin "module_". La última edición no trae ninguno.
    $todos = 'video', 'playback', 'anpr', 'alarms', 'access', 'videowall', 'speakers', 'intercom', 'automation'
    $ediciones = @($todos | ForEach-Object { $sin = $_; ,@($todos | Where-Object { $_ -ne $sin }) }) + ,@('ninguno')
    $temporal = Join-Path ([IO.Path]::GetTempPath()) 'manual-referencias.pdf'
    $fallas = 0
    # PowerShell 5.1 convierte el stderr de typst en error terminante con 'Stop'.
    $ErrorActionPreference = 'Continue'
    foreach ($edicion in $ediciones) {
        $lista = $edicion -join ','
        $salidaTypst = & $typst.Source compile (Join-Path $PSScriptRoot 'manual.typ') $temporal `
            --input "version=$version" --input "modulos=$lista" --input 'referencias=true' 2>&1
        if ($LASTEXITCODE -ne 0) {
            $fallas++
            Write-Host "Edición [$lista]:" -ForegroundColor Red
            $salidaTypst | ForEach-Object { Write-Host "  $_" }
        }
    }
    Remove-Item $temporal -ErrorAction SilentlyContinue
    if ($fallas) { throw "$fallas edición(es) con referencias colgando." }
    Write-Host "Referencias correctas en las $($ediciones.Count) ediciones revisadas."
    return
}

$argumentos = @(
    $(if ($Watch) { 'watch' } else { 'compile' }),
    (Join-Path $PSScriptRoot 'manual.typ'), $Salida,
    '--input', "version=$version"
)
if ($Modulos.Count) { $argumentos += '--input', ('modulos=' + ($Modulos -join ',')) }
if ($Final) { $argumentos += '--input', 'final=true' }

& $typst.Source @argumentos
if ($LASTEXITCODE -ne 0) { throw "typst terminó con código $LASTEXITCODE" }
if (-not $Watch) { Write-Host "Manual: $Salida" }
