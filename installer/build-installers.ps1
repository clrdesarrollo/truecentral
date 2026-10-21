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
       dist\truecentral-screen-<versión>-linux-arm64.tar.gz (app de las pantallas de alarma; va en la suite)

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1 -Version 0.2.0 -Solo Client
  powershell -ExecutionPolicy Bypass -File installer\build-installers.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    # Versión x.y.z; por defecto se lee del archivo VERSION en la raíz del repo.
    [string]$Version,
    # Qué compilar: uno o varios de Suite, Client, Complemento, Migrador
    # (separados por coma, p. ej. -Solo Suite,Client). 'Ambos' = suite, cliente,
    # complemento y migrador (comportamiento histórico); 'Todos' es sinónimo.
    [string[]]$Solo = @('Ambos'),
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

# Vía -File, "Suite,Client" llega como UN string: se separa y valida a mano.
$validos = 'Suite', 'Client', 'Complemento', 'Migrador', 'Ambos', 'Todos'
$Solo = @($Solo | ForEach-Object { $_ -split '[,;\s]+' } | Where-Object { $_ })
foreach ($x in $Solo) {
    if ($validos -notcontains $x) { throw "-Solo '$x' no es válido. Use: $($validos -join ', ')." }
}
$todo = ($Solo -contains 'Ambos') -or ($Solo -contains 'Todos')

# La suite empaqueta el instalador del cliente: compilar la suite implica
# compilar (o tener ya compilado) el del cliente.
$buildSuite  = ($todo -or $Solo -contains 'Suite')
$buildClient = ($todo -or $Solo -contains 'Client')
# El complemento de enrolamiento (lector de huellas USB) va con la suite: es
# ella la que lo publica para que el panel lo ofrezca en descarga.
$buildAgent  = ($todo -or $Solo -contains 'Suite' -or $Solo -contains 'Complemento')
# El migrador desde HikCentral es una herramienta de puesta en marcha: se
# entrega como carpeta comprimida (se ejecuta donde haga falta, sin instalar).
$buildMigrador = ($todo -or $Solo -contains 'Migrador')
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

# App de las pantallas de alarma (Raspberry Pi 4/5, linux-arm64, autocontenida).
# El paquete va a dist\ (para instalar una Pi nueva con installer\screen\install-screen.sh)
# y dentro de la suite (screen-packages\, de donde las Pi ya instaladas se actualizan solas).
function Invoke-PublishScreen([string]$OutDir, [string]$Package) {
    Write-Host ""
    Write-Host "== dotnet publish TrueCentralVms.AlarmScreen linux-arm64  (v$script:Version) ==" -ForegroundColor Cyan
    if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
    dotnet publish (Join-Path $repo 'src\TrueCentralVms.AlarmScreen\TrueCentralVms.AlarmScreen.csproj') -c Release `
        -r linux-arm64 --self-contained true "-p:Version=$script:Version" -o $OutDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló para la app de las pantallas de alarma." }
    Get-ChildItem $OutDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Package) | Out-Null
    if (Test-Path $Package) { Remove-Item -Force $Package }
    # tar.exe de Windows (bsdtar): el paquete se abre en la Pi con tar -xzf.
    & "$env:SystemRoot\System32\tar.exe" -czf $Package -C $OutDir .
    if ($LASTEXITCODE -ne 0) { throw "No se pudo comprimir el paquete de la app de las pantallas de alarma." }
    Write-Host ("  {0}  ({1:N1} MB)" -f (Split-Path -Leaf $Package), ((Get-Item $Package).Length / 1MB)) -ForegroundColor DarkGray
    Protect-ScreenPackage $Package
}

function Find-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($candidate in @("$env:ProgramFiles\Git\usr\bin\openssl.exe", "$env:ProgramFiles\Git\mingw64\bin\openssl.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# Firma del paquete de las pantallas: un manifiesto (producto, versión, archivo y
# SHA-256) firmado con Ed25519. La Pi lo verifica con la llave pública que trae su
# actualizador y no instala nada sin firma válida: así ni un servidor comprometido
# puede empujarles código. La llave privada NO va en git: TCVMS_UPDATE_SIGNING_KEY
# (ruta, credencial de archivo en Jenkins) o installer\config\update-signing.key.
function Protect-ScreenPackage([string]$Package) {
    foreach ($old in @("$Package.manifest", "$Package.manifest.sig")) { if (Test-Path $old) { Remove-Item -Force $old } }
    $key = $env:TCVMS_UPDATE_SIGNING_KEY
    if (-not $key) { $key = Join-Path $PSScriptRoot 'config\update-signing.key' }
    if (-not (Test-Path $key)) {
        Write-Warning ("Paquete de las pantallas SIN FIRMA (falta TCVMS_UPDATE_SIGNING_KEY o installer\config\update-signing.key): " +
            "las pantallas instaladas no lo aceptarán como actualización.")
        return
    }
    $openssl = Find-OpenSsl
    if (-not $openssl) { throw 'Para firmar el paquete de las pantallas hace falta openssl (viene con Git for Windows).' }
    $sha = (Get-FileHash -Algorithm SHA256 $Package).Hash.ToLowerInvariant()
    $manifest = "producto=truecentral-screen`nversion=$script:Version`narchivo=$(Split-Path -Leaf $Package)`nsha256=$sha`n"
    [IO.File]::WriteAllText("$Package.manifest", $manifest, (New-Object System.Text.UTF8Encoding($false)))
    $raw = "$Package.manifest.sig.bin"
    & $openssl pkeyutl -sign -inkey $key -rawin -in "$Package.manifest" -out $raw
    if ($LASTEXITCODE -ne 0) { throw 'openssl no pudo firmar el paquete de las pantallas.' }
    # La firma viaja en Base64 (una línea): es lo que lee el actualizador de la Pi.
    [IO.File]::WriteAllText("$Package.manifest.sig", [Convert]::ToBase64String([IO.File]::ReadAllBytes($raw)), (New-Object System.Text.UTF8Encoding($false)))
    Remove-Item -Force $raw
    Write-Host "  Paquete de las pantallas firmado (Ed25519)." -ForegroundColor DarkGray
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
        foreach ($resto in 'appsettings.Local.json', 'appsettings.Development.json', 'mediamtx.runtime.yml', 'mediamtx.runtime.yml.tmp',
                           'pgdata', 'anpr', 'workflows', 'admin-initial.txt') {
            $ruta = Join-Path $publish "server\$resto"
            if (Test-Path $ruta) { Remove-Item -Recurse -Force $ruta }
        }
        Get-ChildItem (Join-Path $publish 'server') -Filter '*.log' -ErrorAction SilentlyContinue | Remove-Item -Force
        # Servicio ISUP interno: va en la MISMA carpeta que el servidor (comparte su runtime autocontenido) con el SDK ISUP en
        # isup\. Se publica aparte y sin limpiar la carpeta: la referencia del servidor lo dejó dependiente del framework.
        Write-Host ""
        Write-Host "== dotnet publish src\TrueCentralVms.IsupHost  (v$script:Version) ==" -ForegroundColor Cyan
        dotnet publish (Join-Path $repo 'src\TrueCentralVms.IsupHost\TrueCentralVms.IsupHost.csproj') -c Release -r win-x64 --self-contained true `
            "-p:Version=$script:Version" -o (Join-Path $publish 'server') --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló para TrueCentralVms.IsupHost." }
        # Watchdog: monitor de escritorio del servidor, va dentro de la suite
        # (se instala en {app}\watchdog).
        Invoke-Publish 'src\TrueCentralVms.Watchdog\TrueCentralVms.Watchdog.csproj' (Join-Path $publish 'watchdog')
        # App de las pantallas de alarma de las casetas (la suite la publica para las Pi).
        Invoke-PublishScreen (Join-Path $publish 'screen-pi') (Join-Path $dist "truecentral-screen-$Version-linux-arm64.tar.gz")
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
    # Servicio ISUP interno: OBLIGATORIO. Los paneles AX PRO y los equipos detras
    # de NAT/4G se conectan por ISUP contra el, asi que la suite no se entrega sin el.
    Assert-File (Join-Path $publish 'server\TrueCentralVms.IsupHost.exe') 'El publish del servidor no incluyó el servicio ISUP interno.'
    Assert-File (Join-Path $publish 'server\isup\HCISUPCMS.dll') "El publish del servicio ISUP no incluyó el SDK ISUP (native\hikvision-isup). $setupHint"

    # PostgreSQL (build MSVC de EDB) no arranca sin el runtime de Visual C++, que
    # NO viene con Windows: en un servidor limpio initdb.exe muere con
    # 0xC0000135. Se distribuye junto a los .exe (lo copia build\setup-binaries.ps1).
    foreach ($crt in 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'MSVCP140.dll') {
        Assert-File (Join-Path $repo "tools\postgres\pgsql\bin\$crt") `
            "PostgreSQL no arrancara en un equipo sin el redistribuible de Visual C++. Ejecute build\setup-binaries.ps1 para copiarlo junto a los binarios."
    }
    Assert-File (Join-Path $repo 'tools\mediamtx\mediamtx.exe') "Copie MediaMTX v1.21.2 en tools\mediamtx. $setupHint"
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

function Invoke-Iscc([string]$Script, [string[]]$Defines = @()) {
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
                -ArgumentList (@("/DAppVersion=$script:Version") + $Defines + @('/Qp', "`"$issPath`"")) `
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
# Instalador más reciente de dist\ para un prefijo ("...-Setup-"), por número de versión.
function Find-LatestSetup([string]$Prefix) {
    Get-ChildItem $dist -Filter "$Prefix*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName.Substring($Prefix.Length) -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.BaseName.Substring($Prefix.Length) } -Descending |
        Select-Object -First 1
}

if ($buildSuite) {
    # Compilar solo la suite (-Solo Suite): si el cliente o el complemento de ESTA versión no
    # existen, se empaqueta el último que haya en dist\ (no cambian con cada versión del
    # servidor y el servidor los publica sin mirar la versión). Así no se pierden al actualizar.
    $defines = @()
    foreach ($pkg in @(
            @{ Prefix = 'CLRTrueCentralVMS-Client-Setup-';      Define = 'ClientSetupName'; Name = 'cliente de escritorio' },
            @{ Prefix = 'CLRTrueCentralVMS-Complemento-Setup-'; Define = 'AgentSetupName';  Name = 'complemento de enrolamiento' })) {
        $current = Join-Path $dist "$($pkg.Prefix)$Version.exe"
        if (Test-Path $current) { continue }
        $latest = Find-LatestSetup $pkg.Prefix
        if ($latest) {
            Write-Host ("  La suite reutiliza el {0} existente: {1}" -f $pkg.Name, $latest.Name) -ForegroundColor Yellow
            $defines += "/D$($pkg.Define)=$($latest.Name)"
        } else {
            Write-Warning ("No hay ningún instalador del $($pkg.Name) en ${dist}: la suite se compilará SIN él. " +
                "Compile con -Solo Ambos para incluirlo.")
        }
    }

    # API key del producto en el servidor de licencias (activación en línea).
    # NO va en git: sale de la variable TCVMS_LICENSE_APIKEY (Jenkins) o del
    # archivo local installer\config\licensing.apikey, y se escribe en una COPIA
    # de appsettings.Production.json que es la que empaqueta la suite. Sin key
    # se compila igual, pero los servidores instalados solo podrán activarse
    # sin conexión (.req / .lic).
    $apiKey = $env:TCVMS_LICENSE_APIKEY
    $apiKeySource = 'variable TCVMS_LICENSE_APIKEY'
    $apiKeyFile = Join-Path $PSScriptRoot 'config\licensing.apikey'
    if (-not $apiKey -and (Test-Path $apiKeyFile)) {
        $apiKey = (Get-Content $apiKeyFile -Raw)
        $apiKeySource = 'installer\config\licensing.apikey'
    }
    $apiKey = "$apiKey".Trim()
    if ($apiKey) {
        if ($apiKey -notmatch '^[A-Za-z0-9._-]+$') { throw 'La API key del servidor de licencias tiene caracteres inesperados.' }
        $template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'config\appsettings.Production.json'))
        $withKey = [regex]::Replace($template, '("ApiKey"\s*:\s*)"[^"]*"', ('${1}"' + $apiKey + '"'))
        if ($withKey -eq $template) { throw 'No se encontró "ApiKey" en installer\config\appsettings.Production.json.' }
        $configDir = Join-Path $publish 'installer-config'
        New-Item -ItemType Directory -Force -Path $configDir | Out-Null
        $configPath = Join-Path $configDir 'appsettings.Production.json'
        [IO.File]::WriteAllText($configPath, $withKey, (New-Object System.Text.UTF8Encoding($true)))
        # Entre comillas: la ruta del repositorio tiene espacios.
        $defines += "/DProductionConfig=`"$configPath`""
        Write-Host "  Servidor de licencias: API key incluida (de $apiKeySource)." -ForegroundColor DarkGray
    } else {
        Write-Warning ("Sin API key del servidor de licencias (TCVMS_LICENSE_APIKEY o installer\config\licensing.apikey): " +
            "los servidores instalados solo podrán activarse sin conexión.")
    }
    Invoke-Iscc 'suite.iss' $defines
}

# ---------------------------------------------------------------------------
# 4) Resumen
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Instaladores generados en $dist :" -ForegroundColor Green
Get-ChildItem $dist -Include "*-$Version.exe", "*-$Version.zip" -Recurse -Depth 0 | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
