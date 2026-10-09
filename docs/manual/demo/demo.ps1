<#
.SYNOPSIS
  Entorno de demostración para las capturas del manual: servidor y cliente
  compilados del último commit, base de datos propia, cámaras ONVIF simuladas
  y datos ficticios. No toca el servicio instalado ni el servidor de desarrollo.

.EXAMPLE
  .\demo.ps1 Preparar   # compila servidor y cliente desde HEAD (una vez, o tras cambios)
  .\demo.ps1 Iniciar    # levanta cámaras y servidor (http://127.0.0.1:5390)
  .\demo.ps1 Sembrar    # carga los datos de demostración (servidor recién creado)
  .\demo.ps1 Estado
  .\demo.ps1 Detener
  .\demo.ps1 Borrar     # detiene y borra la base de datos (para sembrar de cero)
#>
param(
    [Parameter(Mandatory)][ValidateSet('Preparar', 'Iniciar', 'Sembrar', 'Estado', 'Detener', 'Borrar')]
    [string]$Accion,
    # Preparar: recompila solo el cliente (se puede con el servidor corriendo).
    [switch]$SoloCliente
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$configRuta = Join-Path $PSScriptRoot 'demo.json'
$config = Get-Content $configRuta -Raw -Encoding UTF8 | ConvertFrom-Json
$datos = [Environment]::ExpandEnvironmentVariables($config.carpetaDatos)
$p = $config.puertos

$srcDir = Join-Path $datos 'src'
$servidorDir = Join-Path $datos 'servidor'
$clienteDir = Join-Path $datos 'cliente'
$pgData = Join-Path $datos 'pgdata'
$logs = Join-Path $datos 'logs'
$pidsRuta = Join-Path $datos 'procesos.json'
$pgBin = Join-Path $repo 'tools\postgres\pgsql\bin'

function Paso([string]$texto) { Write-Host "» $texto" -ForegroundColor Cyan }

function Es-Enlace([string]$ruta) {
    (Test-Path $ruta) -and ((Get-Item $ruta -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

# Remove-Item de PowerShell 5.1 puede entrar en una unión y borrar su destino:
# las uniones se quitan con rmdir, que solo borra el enlace.
function Quitar-Enlace([string]$ruta) {
    if (Es-Enlace $ruta) { cmd /c rmdir "$ruta" | Out-Null }
}

function Puerto-Ocupado([int]$puerto) {
    [bool](Get-NetTCPConnection -State Listen -LocalPort $puerto -ErrorAction SilentlyContinue)
}

function Leer-Procesos {
    if (Test-Path $pidsRuta) { Get-Content $pidsRuta -Raw | ConvertFrom-Json } else { $null }
}

function Proceso-Vivo($id) {
    $id -and [bool](Get-Process -Id $id -ErrorAction SilentlyContinue)
}

function Copiar([string]$origen, [string]$destino) {
    New-Item -ItemType Directory -Force (Split-Path $destino) | Out-Null
    Copy-Item $origen $destino -Force
}

switch ($Accion) {
    'Preparar' {
        foreach ($necesario in @('native', 'tools\mediamtx\mediamtx.exe', 'tools\ffmpeg\bin\ffmpeg.exe', 'tools\ffmpeg-flyleaf', 'tools\postgres\pgsql\bin')) {
            if (-not (Test-Path (Join-Path $repo $necesario))) { throw "Falta $necesario en el repositorio (build\setup-binaries.ps1)." }
        }
        $procesos = Leer-Procesos
        if (-not $SoloCliente -and $procesos -and (Proceso-Vivo $procesos.servidor)) { throw 'El servidor de demostración está corriendo: deténgalo antes de preparar.' }

        New-Item -ItemType Directory -Force $datos, $logs | Out-Null
        $commit = (git -C $repo rev-parse --short HEAD).Trim()
        Paso "Código del commit $commit (sin cambios locales)"
        Quitar-Enlace (Join-Path $srcDir 'native')
        if (Test-Path $srcDir) { Remove-Item $srcDir -Recurse -Force }
        New-Item -ItemType Directory -Force $srcDir | Out-Null
        $tar = Join-Path $datos 'src.tar'
        git -C $repo archive -o $tar HEAD
        tar -xf $tar -C $srcDir
        Remove-Item $tar

        # Solo en esta copia: el cliente de la demo guarda su configuración donde diga
        # TCVMS_DEMO_APPDATA, para no tocar el client.json del cliente instalado (que
        # puede estar abierto a la vez). Si el código cambia y el reemplazo ya no
        # calza, falla aquí en vez de pisar la configuración real.
        $ajustes = Join-Path $srcDir 'src\TrueCentralVms.Client\Services\ClientSettings.cs'
        $original = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),'
        $texto = [IO.File]::ReadAllText($ajustes)
        if (([regex]::Matches($texto, [regex]::Escape($original))).Count -ne 1) { throw "No se pudo redirigir la configuración del cliente: revise $ajustes." }
        [IO.File]::WriteAllText($ajustes, $texto.Replace($original,
            'Environment.GetEnvironmentVariable("TCVMS_DEMO_APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),'))

        # Los proyectos copian los SDK nativos desde ..\..\native: unión temporal al del repositorio.
        cmd /c mklink /J "$(Join-Path $srcDir 'native')" "$(Join-Path $repo 'native')" | Out-Null
        try {
            if (-not $SoloCliente) {
                Paso 'Compilando el servidor'
                dotnet publish (Join-Path $srcDir 'src\TrueCentralVms.Server\TrueCentralVms.Server.csproj') -c Release -o $servidorDir --nologo -v quiet
                if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del servidor.' }
            }
            Paso 'Compilando el cliente'
            dotnet publish (Join-Path $srcDir 'src\TrueCentralVms.Client\TrueCentralVms.Client.csproj') -c Release -o $clienteDir --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del cliente.' }
        }
        finally {
            Quitar-Enlace (Join-Path $srcDir 'native')
        }
        Set-Content (Join-Path $datos 'cliente-commit.txt') $commit -Encoding ASCII
        if ($SoloCliente) { Paso "Cliente listo en $clienteDir"; return }

        # El servidor y el cliente buscan tools\ subiendo desde su carpeta. MediaMTX va
        # copiado (dos veces): el servidor cierra los mediamtx que usan su mismo exe.
        Paso 'Copiando herramientas'
        Copiar (Join-Path $repo 'tools\mediamtx\mediamtx.exe') (Join-Path $datos 'tools\mediamtx\mediamtx.exe')
        Copiar (Join-Path $repo 'tools\mediamtx\mediamtx.exe') (Join-Path $datos 'camaras\mediamtx.exe')
        Copiar (Join-Path $repo 'tools\ffmpeg\bin\ffmpeg.exe') (Join-Path $datos 'tools\ffmpeg\bin\ffmpeg.exe')
        robocopy (Join-Path $repo 'tools\ffmpeg-flyleaf') (Join-Path $datos 'tools\ffmpeg-flyleaf') /E /NJH /NJS /NFL /NDL | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'Falló la copia de ffmpeg-flyleaf.' }
        $global:LASTEXITCODE = 0  # robocopy: 1 = copió archivos
        Set-Content (Join-Path $datos 'commit.txt') $commit -Encoding ASCII
        Paso "Listo en $datos"
    }

    'Iniciar' {
        if (-not (Test-Path (Join-Path $servidorDir 'TrueCentralVms.Server.exe'))) { throw 'Falta preparar: .\demo.ps1 Preparar' }
        $procesos = Leer-Procesos
        if ($procesos -and (Proceso-Vivo $procesos.servidor)) { Write-Host 'Ya está corriendo.'; return }
        $puertos = @($p.web, $p.postgres, $p.rtsp, $p.apiVideo, $p.webrtc, $p.webrtcIce, $p.camarasRtsp) + @($config.camaras | ForEach-Object { $_.puerto })
        $ocupados = $puertos | Where-Object { Puerto-Ocupado $_ }
        if ($ocupados) { throw "Puertos ocupados: $($ocupados -join ', ')" }
        New-Item -ItemType Directory -Force $logs | Out-Null

        Paso 'Cámaras simuladas'
        $python = (Get-Command python).Source
        $camaras = Start-Process $python -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $logs 'camaras.log') -RedirectStandardError (Join-Path $logs 'camaras.err.log') `
            -ArgumentList @("`"$(Join-Path $PSScriptRoot 'camaras.py')`"", "`"$configRuta`"", "`"$datos`"",
                "`"$(Join-Path $datos 'tools\ffmpeg\bin\ffmpeg.exe')`"", "`"$(Join-Path $datos 'camaras\mediamtx.exe')`"")

        Paso 'Servidor'
        $argumentos = @(
            "--Urls=http://127.0.0.1:$($p.web)",
            "--Database:PgPort=$($p.postgres)",
            "--Database:PgDataDir=$pgData",
            "`"--Database:PgBinDir=$pgBin`"",
            "--Streaming:RtspPort=$($p.rtsp)",
            "--Streaming:ApiPort=$($p.apiVideo)",
            "--Streaming:WebRtcPort=$($p.webrtc)",
            "--Streaming:WebRtcIcePort=$($p.webrtcIce)",
            "--Streaming:ServerPort=$($p.web)",
            '--Alarms:Receiver:Enabled=false',
            '--Alarms:LocalReceiver:Enabled=false',
            '--Cerco:Receiver:Enabled=false',
            '--Cerco:Firmware:Enabled=false',
            '--Licensing:ServerUrl='
        )
        $servidor = Start-Process (Join-Path $servidorDir 'TrueCentralVms.Server.exe') -PassThru -WindowStyle Hidden `
            -WorkingDirectory $servidorDir -ArgumentList $argumentos `
            -RedirectStandardOutput (Join-Path $logs 'servidor.log') -RedirectStandardError (Join-Path $logs 'servidor.err.log')
        @{ camaras = $camaras.Id; servidor = $servidor.Id } | ConvertTo-Json | Set-Content $pidsRuta -Encoding ASCII

        $url = "http://127.0.0.1:$($p.web)/"
        foreach ($i in 1..90) {
            Start-Sleep -Seconds 1
            if ($servidor.HasExited) { throw "El servidor terminó (código $($servidor.ExitCode)). Revise $logs\servidor.log" }
            try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; Paso "Servidor listo: $url"; return } catch { }
        }
        throw "El servidor no respondió en 90 s. Revise $logs\servidor.log"
    }

    'Sembrar' {
        $python = (Get-Command python).Source
        & $python (Join-Path $PSScriptRoot 'sembrar.py') $configRuta $datos
        if ($LASTEXITCODE -ne 0) { throw 'Falló la siembra.' }
    }

    'Estado' {
        $procesos = Leer-Procesos
        Write-Host "Datos:    $datos (commit $(Get-Content (Join-Path $datos 'commit.txt') -ErrorAction SilentlyContinue))"
        Write-Host "Servidor: $(if ($procesos -and (Proceso-Vivo $procesos.servidor)) { "corriendo (PID $($procesos.servidor)) en http://127.0.0.1:$($p.web)/" } else { 'detenido' })"
        Write-Host "Cámaras:  $(if ($procesos -and (Proceso-Vivo $procesos.camaras)) { "corriendo (PID $($procesos.camaras))" } else { 'detenidas' })"
    }

    { $_ -in 'Detener', 'Borrar' } {
        $procesos = Leer-Procesos
        if ($procesos) {
            foreach ($id in @($procesos.servidor, $procesos.camaras)) {
                if (Proceso-Vivo $id) { taskkill /T /F /PID $id | Out-Null }
            }
            Remove-Item $pidsRuta
        }
        # PostgreSQL queda fuera del árbol del servidor.
        if (Test-Path (Join-Path $pgData 'postmaster.pid')) {
            & (Join-Path $pgBin 'pg_ctl.exe') stop -D $pgData -m fast 2>&1 | Out-Null
        }
        Paso 'Detenido'
        if ($Accion -eq 'Borrar') {
            # Los secretos del servidor (tcvms-*) van con la base: sin ella no sirven. La
            # licencia (período de prueba) vive junto a pgdata y también vuelve a cero.
            foreach ($ruta in @($pgData, (Join-Path $datos 'license'), (Join-Path $servidorDir 'mediamtx.runtime.yml'))) {
                if (Test-Path $ruta) { Remove-Item $ruta -Recurse -Force }
            }
            Get-ChildItem $servidorDir -Filter 'tcvms-*' -ErrorAction SilentlyContinue | Remove-Item -Force
            Paso 'Base de datos borrada'
        }
    }
}
