<#
.SYNOPSIS
  Compila el manual de usuario (Typst) a PDF con la versión del archivo VERSION
  y, con -Web, también como sitio HTML desde los mismos capítulos.

.EXAMPLE
  .\build-manual.ps1                          # dist\CLRTrueCentralVMS-Manual-<versión>.pdf
  .\build-manual.ps1 -Web                     # además dist\CLRTrueCentralVMS-Manual-<versión>-web\
  .\build-manual.ps1 -Watch                   # recompila cada vez que se guarda un archivo
  .\build-manual.ps1 -Watch -Web              # el sitio, servido en http://localhost:3000 y
                                              # recargado solo al guardar
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
    [switch]$Web,
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
    $todos = 'video', 'playback', 'anpr', 'alarms', 'access', 'videowall', 'speakers', 'io', 'alarm_screens', 'intercom', 'automation'
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

$comunes = @('--input', "version=$version")
if ($Modulos.Count) { $comunes += '--input', ('modulos=' + ($Modulos -join ',')) }
if ($Final) { $comunes += '--input', 'final=true' }

# El sitio usa la exportación "bundle" de Typst (experimental): una página por
# capítulo, capturas como archivos y el PDF al lado para descargarlo.
$sitio = Join-Path (Split-Path $Salida) ([IO.Path]::GetFileNameWithoutExtension($Salida) + '-web')
$argumentosWeb = @('--features', 'html,bundle', '--format', 'bundle', (Join-Path $PSScriptRoot 'manual-web.typ'), $sitio) + $comunes

if ($Watch) {
    if ($Web) { & $typst.Source watch @argumentosWeb --input "pdf=$(Split-Path $Salida -Leaf)" }
    else { & $typst.Source watch (Join-Path $PSScriptRoot 'manual.typ') $Salida @comunes }
    return
}

& $typst.Source compile (Join-Path $PSScriptRoot 'manual.typ') $Salida @comunes
if ($LASTEXITCODE -ne 0) { throw "typst terminó con código $LASTEXITCODE" }
Write-Host "Manual: $Salida"

if ($Web) {
    if (Test-Path $sitio) { Remove-Item $sitio -Recurse -Force }
    # PowerShell 5.1 convierte en error el aviso de "experimental" que typst escribe en stderr.
    $ErrorActionPreference = 'Continue'
    & $typst.Source compile @argumentosWeb --input "pdf=$(Split-Path $Salida -Leaf)" 2>&1 |
        ForEach-Object { if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { "$_" } } |
        Where-Object { $_ -notmatch 'is experimental|under active development|behaviour may change|do not rely on|issues/5512|^\s*$' } |
        ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "typst terminó con código $LASTEXITCODE al generar el sitio" }
    $ErrorActionPreference = 'Stop'
    Copy-Item $Salida $sitio
    Write-Host "Sitio: $sitio\index.html"
}
