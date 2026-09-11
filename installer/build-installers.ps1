<#
.SYNOPSIS
  Compila los instaladores de CLR TrueCentral VMS: la SUITE completa (servidor
  como servicio de Windows + panel web + PostgreSQL/MediaMTX/FFmpeg embebidos,
  con el cliente de escritorio opcional) y el CLIENTE solo (puestos de
  operación).

.DESCRIPTION
  1. Lee la versión del archivo VERSION (o del parámetro -Version).
  2. Publica servidor, Watchdog y cliente (.NET self-contained win-x64: los
     equipos destino NO necesitan tener .NET instalado).
  3. Verifica que los binarios que no van al repositorio estén presentes
     (native\ y tools\: build\setup-binaries.ps1).
  4. Compila los .iss con Inno Setup 6 y deja los instaladores en dist\:
       dist\CLRTrueCentralVMS-Client-Setup-<versión>.exe
       dist\CLRTrueCentralVMS-Complemento-Setup-<versión>.exe  (lector de huellas)
       dist\CLRTrueCentralVMS-Suite-Setup-<versión>.exe   (incluye los dos anteriores)
       dist\CLRTrueCentralVMS-Migrador-<versión>.zip       (migrador desde HikCentral, sin instalador)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1 -Version 0.2.0 -Solo Client
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    # Versión x.y.z; por defecto se lee del archivo VERSION en la raíz del repo.
    [string]$Version,
    # Compilar solo uno de los instaladores ('Ambos' = suite y cliente).
    [ValidateSet('Suite', 'Client', 'Complemento', 'Migrador', 'Ambos')]
    [string]$Solo = 'Ambos',
    # Reutilizar build\publish existente (no vuelve a publicar los proyectos).
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $versionFile = Join-Path $repo 'VERSION'
    if (-not (Test-Path $versionFile)) { throw "No existe $versionFile ni se indicó -Version." }
    $Version = (Get-Content $versionFile -TotalCount 1).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versión inválida: '$Version' (se espera x.y.z)." }

# La suite empaqueta el instalador del cliente: compilar la suite implica
# compilar (o tener ya compilado) el del cliente.
$buildSuite  = ($Solo -eq 'Ambos' -or $Solo -eq 'Suite')
$buildClient = ($Solo -eq 'Ambos' -or $Solo -eq 'Client')
# El complemento de enrolamiento (lector de huellas USB) va con la suite: es
# ella la que lo publica para que el panel lo ofrezca en descarga.
$buildAgent  = ($Solo -eq 'Ambos' -or $Solo -eq 'Suite' -or $Solo -eq 'Complemento')
# El migrador desde HikCentral es una herramienta de puesta en marcha: se
# entrega como carpeta comprimida (se ejecuta donde haga falta, sin instalar).
$buildMigrador = ($Solo -eq 'Ambos' -or $Solo -eq 'Migrador')
$publish = Join-Path $repo 'build\publish'
$dist = Join-Path $repo 'dist'

function Invoke-Publish([string]$Project, [string]$OutDir) {
    Write-Host ""
    Write-Host "== dotnet publish $Project  (v$script:Version) ==" -ForegroundColor Cyan
    # Salida limpia: un publish anterior podría dejar archivos que ya no existen.
    if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
    dotnet publish (Join-Path $repo $Project) -c Release -r win-x64 --self-contained true `
        "-p:Version=$script:Version" -o $OutDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló para $Project." }
}

function Assert-File([string]$Path, [string]$Hint) {
    if (-not (Test-Path $Path)) { throw "Falta '$Path'. $Hint" }
}

# ---------------------------------------------------------------------------
# 1) Publicar
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    if ($buildSuite) {
        Invoke-Publish 'src\TrueCentralVms.Server\TrueCentralVms.Server.csproj' (Join-Path $publish 'server')
        # El SDK Web publica todo *.json del proyecto como contenido: los restos
        # de desarrollo (ajustes locales, datos de runtime) no van al instalador.
        foreach ($resto in 'appsettings.Local.json', 'appsettings.Development.json', 'mediamtx.runtime.yml',
                           'pgdata', 'anpr', 'workflows', 'admin-initial.txt') {
            $ruta = Join-Path $publish "server\$resto"
            if (Test-Path $ruta) { Remove-Item -Recurse -Force $ruta }
        }
        Get-ChildItem (Join-Path $publish 'server') -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force
        # Watchdog: monitor de escritorio del servidor, va dentro de la suite
        # (se instala en {app}\watchdog).
        Invoke-Publish 'src\TrueCentralVms.Watchdog\TrueCentralVms.Watchdog.csproj' (Join-Path $publish 'watchdog')
    }
    if ($buildClient) {
        Invoke-Publish 'src\TrueCentralVms.Client\TrueCentralVms.Client.csproj' (Join-Path $publish 'client')
    }
    if ($buildAgent) {
        Invoke-Publish 'src\TrueCentralVms.WebControl\TrueCentralVms.WebControl.csproj' (Join-Path $publish 'complemento')
    }
    if ($buildMigrador) {
        Invoke-Publish 'src\TrueCentralVms.Migrator\TrueCentralVms.Migrator.csproj' (Join-Path $publish 'migrador')
    }
}

# ---------------------------------------------------------------------------
# 2) Verificaciones (binarios que no van al repositorio)
# ---------------------------------------------------------------------------
$setupHint = 'Ejecute build\setup-binaries.ps1 (ver README).'
if ($buildSuite) {
    Assert-File (Join-Path $publish 'server\TrueCentralVms.Server.exe') 'Publique primero (quite -SkipPublish).'
    Assert-File (Join-Path $publish 'server\HCNetSDK.dll') "El publish del servidor no incluyó las DLL nativas de Hikvision (native\hikvision). $setupHint"
    Assert-File (Join-Path $publish 'server\dhnetsdk.dll') "El publish del servidor no incluyó las DLL nativas de Dahua (native\dahua). $setupHint"
    Assert-File (Join-Path $publish 'server\wwwroot\index.html') 'El publish del servidor no incluyó el panel web (wwwroot).'
    Assert-File (Join-Path $publish 'watchdog\TrueCentralVms.Watchdog.exe') 'Publique primero (quite -SkipPublish).'
    Assert-File (Join-Path $repo 'tools\postgres\pgsql\bin\pg_ctl.exe') "Copie los binarios oficiales de PostgreSQL x64 (bin\lib\share) en tools\postgres\pgsql. $setupHint"
    # Receptor de paneles de alarma: OBLIGATORIO. Los paneles AX PRO y AX HYBRID
    # PRO reportan por ISUP/OTAP contra el, asi que la suite no se entrega sin el.
    $iprpSetup = Join-Path $repo 'tools\iprp\HikIpReceiverPro-Setup.exe'
    Assert-File $iprpSetup ("Copie el instalador oficial del Hik IP Receiver Pro (V2.5.0 o superior) como " +
        "tools\iprp\HikIpReceiverPro-Setup.exe. $setupHint")
    Write-Host ("  Hik IP Receiver Pro: {0:N0} MB" -f ((Get-Item $iprpSetup).Length / 1MB)) -ForegroundColor DarkGray

    # PostgreSQL (build MSVC de EDB) no arranca sin el runtime de Visual C++, que
    # NO viene con Windows: en un servidor limpio initdb.exe muere con
    # 0xC0000135. Se distribuye junto a los .exe (lo copia build\setup-binaries.ps1).
    foreach ($crt in 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll') {
        Assert-File (Join-Path $repo "tools\postgres\pgsql\bin\$crt") `
            "PostgreSQL no arrancara en un equipo sin el redistribuible de Visual C++. Ejecute build\setup-binaries.ps1 para copiarlo junto a los binarios."
    }
    Assert-File (Join-Path $repo 'tools\mediamtx\mediamtx.exe') "Copie MediaMTX v1.20 en tools\mediamtx. $setupHint"
    Assert-File (Join-Path $repo 'tools\ffmpeg\bin\ffmpeg.exe') "Copie FFmpeg (build de gyan.dev) en tools\ffmpeg. $setupHint"
}
if ($buildAgent) {
    Assert-File (Join-Path $publish 'complemento\TrueCentralVms.WebControl.exe') 'Publique primero (quite -SkipPublish).'
    Assert-File (Join-Path $publish 'complemento\FPModule_SDK.dll') ("El publish del complemento no incluyó el SDK del " +
        "lector de huellas (native\hikvision-fp\FPModule_SDK.dll). $setupHint")
}
if ($buildClient) {
    Assert-File (Join-Path $publish 'client\TrueCentralVms.Client.exe') 'Publique primero (quite -SkipPublish).'
    if (-not (Get-ChildItem (Join-Path $repo 'tools\ffmpeg-flyleaf') -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue)) {
        throw "Falta el FFmpeg compartido de FlyleafLib en tools\ffmpeg-flyleaf (avcodec-*.dll). $setupHint"
    }
    Assert-File (Join-Path $repo 'tools\ffmpeg\bin\ffmpeg.exe') "Copie FFmpeg (build de gyan.dev) en tools\ffmpeg. $setupHint"
    Assert-File (Join-Path $repo 'tools\ffmpeg\bin\ffprobe.exe') 'La build de FFmpeg debe incluir ffprobe.exe (duración de archivos al proyectar).'
    Assert-File (Join-Path $repo 'tools\mediamtx\mediamtx.exe') "Copie MediaMTX en tools\mediamtx. $setupHint"
}

# ---------------------------------------------------------------------------
# 3) Compilar instaladores
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if ($buildMigrador) {
    Assert-File (Join-Path $publish 'migrador\TrueCentralMigrador.exe') 'Publique primero (quite -SkipPublish).'
    $zip = Join-Path $dist "CLRTrueCentralVMS-Migrador-$Version.zip"
    Write-Host ""
    Write-Host "== Comprimir migrador -> $zip ==" -ForegroundColor Cyan
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $publish 'migrador\*') -DestinationPath $zip -CompressionLevel Optimal
}

if (-not ($buildSuite -or $buildClient -or $buildAgent)) {
    Write-Host ""
    Write-Host "Generado en $dist :" -ForegroundColor Green
    Get-ChildItem $dist -Filter "*-$Version.zip" | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
    return
}

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')   # instalación winget (usuario)
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
    else { throw 'Inno Setup 6 no está instalado. Instálelo con:  winget install -e --id JRSoftware.InnoSetup' }
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Invoke-Iscc([string]$Script) {
    Write-Host ""
    Write-Host "== ISCC $Script ==" -ForegroundColor Cyan
    # ISCC puede fallar con "Resource update error: EndUpdateResource failed
    # (110)" cuando el antivirus escanea el .exe recién escrito en dist\ y lo
    # bloquea un instante. Es transitorio: se reintenta unas veces antes de
    # rendirse. Solución definitiva: excluir la carpeta dist\ del antivirus.
    $maxAttempts = 3
    $issPath = Join-Path $PSScriptRoot $Script
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        # Salida vía archivos temporales: en Windows PowerShell 5.1 redirigir el
        # stderr de un .exe con 2>&1 lo convierte en error terminante y no
        # llegaría al reintento.
        $outFile = [IO.Path]::GetTempFileName()
        $errFile = [IO.Path]::GetTempFileName()
        try {
            $proc = Start-Process -FilePath $iscc -NoNewWindow -Wait -PassThru `
                -ArgumentList @("/DAppVersion=$script:Version", '/Qp', "`"$issPath`"") `
                -RedirectStandardOutput $outFile -RedirectStandardError $errFile
            $output = (Get-Content $outFile -Raw) + (Get-Content $errFile -Raw)
            $exit = $proc.ExitCode
        }
        finally {
            Remove-Item $outFile, $errFile -ErrorAction SilentlyContinue
        }
        if ($output) { Write-Host $output.TrimEnd() }
        if ($exit -eq 0) { return }

        $transient = $output -match 'EndUpdateResource|antivirus|being used by another process'
        if ($transient -and $attempt -lt $maxAttempts) {
            Write-Warning ("ISCC falló por un bloqueo temporal del archivo de salida (antivirus). " +
                "Reintento $attempt de $($maxAttempts - 1) en 5 s...")
            Start-Sleep -Seconds 5
            continue
        }
        if ($transient) {
            Write-Warning ("Si esto se repite, excluya la carpeta '$dist' del antivirus " +
                "(Windows Defender: Configuración > Protección antivirus > Exclusiones).")
        }
        throw "ISCC falló para $Script."
    }
}

# El cliente va PRIMERO: la suite empaqueta su instalador para poder
# instalarlo en el mismo equipo y dejarlo disponible para los demás puestos.
if ($buildClient) { Invoke-Iscc 'client.iss' }
if ($buildAgent) { Invoke-Iscc 'complemento.iss' }
if ($buildSuite) {
    $clientSetup = Join-Path $dist "CLRTrueCentralVMS-Client-Setup-$Version.exe"
    if (-not (Test-Path $clientSetup)) {
        Write-Warning ("No existe ${clientSetup}: la suite se compilará SIN el cliente de escritorio. " +
            "Compile con -Solo Ambos para incluirlo.")
    }
    Invoke-Iscc 'suite.iss'
}

# ---------------------------------------------------------------------------
# 4) Resumen
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Instaladores generados en $dist :" -ForegroundColor Green
Get-ChildItem $dist -Include "*-$Version.exe", "*-$Version.zip" -Recurse -Depth 0 | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
