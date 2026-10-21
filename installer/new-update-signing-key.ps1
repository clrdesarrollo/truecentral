<#
.SYNOPSIS
    Genera el par de llaves Ed25519 con que se firman las actualizaciones de las pantallas de alarma.

.DESCRIPTION
    La llave PRIVADA queda en installer\config\update-signing.key (fuera de git). Súbala a Jenkins como
    credencial de tipo archivo con el id "truecentral-update-signing-key" y guarde una copia fuera de línea:
    quien la tenga puede publicar actualizaciones que las pantallas aceptan.

    La llave PÚBLICA se imprime al final: va en installer\screen\truecentral-screen-update.sh (UPDATE_PUBKEY).
    Cambiarla exige reinstalar el actualizador en cada pantalla (install-screen.sh), así que solo se rota si la
    privada se compromete.

.PARAMETER Force
    Reemplaza una llave privada existente.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$keyPath = Join-Path $repo 'installer\config\update-signing.key'

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @("$env:ProgramFiles\Git\usr\bin\openssl.exe", "$env:ProgramFiles\Git\mingw64\bin\openssl.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'No se encontró openssl (viene con Git for Windows).'
}

if ((Test-Path $keyPath) -and -not $Force) {
    throw "Ya existe $keyPath. Use -Force solo si quiere reemplazarla (las pantallas instaladas dejarán de aceptar actualizaciones hasta reinstalar su actualizador)."
}
$openssl = Find-OpenSsl
& $openssl genpkey -algorithm ed25519 -out $keyPath
if ($LASTEXITCODE -ne 0) { throw 'openssl no pudo generar la llave.' }
Write-Host "Llave privada: $keyPath" -ForegroundColor Green
Write-Host 'Llave pública (UPDATE_PUBKEY de truecentral-screen-update.sh):' -ForegroundColor Cyan
& $openssl pkey -in $keyPath -pubout
