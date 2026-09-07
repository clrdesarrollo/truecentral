// ---------------------------------------------------------------------------
// CLR TrueCentral VMS — canalización de integración continua
//
// Qué entrega (dist\):
//   CLRTrueCentralVMS-Server-<versión>.zip   API + panel web + drivers +
//                                            PostgreSQL embebido + MediaMTX
//   CLRTrueCentralVMS-Client-<versión>.zip   cliente WPF + FFmpeg de Flyleaf
//
// Requisitos del agente (etiqueta 'windows'):
//   - Windows x64 con el SDK de .NET 10 en el PATH. NO compila en Linux: el
//     cliente es WPF y los SDK de Hikvision/Dahua son nativos de 64 bits.
//   - tar.exe (viene con Windows 10/11) para comprimir los paquetes.
//   - Carpeta BINARIES_DIR con los binarios de terceros que no viven en git
//     (native\ y tools\, cientos de MB). Se genera una vez en una máquina de
//     desarrollo con build\setup-binaries.ps1 y se deja fija en el nodo:
//
//        <BINARIES_DIR>\native\hikvision\HCNetSDK.dll
//        <BINARIES_DIR>\native\dahua\dhnetsdk.dll
//        <BINARIES_DIR>\tools\postgres\pgsql\bin\pg_ctl.exe
//        <BINARIES_DIR>\tools\mediamtx\mediamtx.exe
//        <BINARIES_DIR>\tools\ffmpeg\bin\ffmpeg.exe         (proxy de canales)
//        <BINARIES_DIR>\tools\ffmpeg-flyleaf\avcodec-*.dll  (cliente WPF)
//
//     Sin esa carpeta el build igual compila, pero el paquete sale incompleto
//     y la ejecución queda marcada UNSTABLE.
//
// Nota de codificación: los bloques PowerShell van SIN tildes ni guiones
// largos a propósito. Jenkins escribe cada bloque a un .ps1 temporal y, si el
// agente no lo lee como UTF-8, un guion largo se decodifica como comilla
// tipográfica — que PowerShell trata como delimitador de cadena y revienta el
// parser. Los comentarios y mensajes de Groovy sí llevan acentos.
// ---------------------------------------------------------------------------

// PowerShell no falla porque un ejecutable nativo devuelva != 0: hay que
// propagar $LASTEXITCODE a mano o el pipeline daría verde con la compilación
// rota.
def dotnet(String etiqueta, String argumentos) {
    powershell label: etiqueta, script: """
        dotnet ${argumentos}
        if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }
    """
}

pipeline {
    agent { label 'windows' }

    options {
        // Se guardan 20 builds pero artefactos de solo 2: cada build archiva
        // dos .zip con PostgreSQL, MediaMTX y FFmpeg dentro (cientos de MB cada
        // uno) y eso ocupa el disco del CONTROLADOR, no el del agente.
        buildDiscarder logRotator(artifactNumToKeepStr: '2', numToKeepStr: '20')
        disableConcurrentBuilds()
        timestamps()
        timeout(time: 60, unit: 'MINUTES')
        // El checkout se hace explícito en su etapa (así queda en el log qué
        // commit se compiló).
        skipDefaultCheckout()
    }

    // El disparador normal es el webhook de GitHub (o el escaneo del
    // Multibranch). Esto es la red de seguridad para cuando el hook no llega:
    // el controlador no es alcanzable desde internet, se cayó el túnel, o
    // GitHub tuvo un mal día. La H reparte el sondeo dentro del intervalo para
    // que no salgan todos los jobs del servidor en el mismo segundo.
    triggers {
        pollSCM('H/5 * * * *')
    }

    parameters {
        booleanParam(
            name: 'SELF_CONTAINED', defaultValue: true,
            description: 'Publicar con el runtime .NET incluido (el sitio del cliente no necesita instalar .NET 10).')
        booleanParam(
            name: 'RUN_AUDIT', defaultValue: true,
            description: 'Auditar dependencias NuGet vulnerables. Requiere salida a nuget.org.')
        booleanParam(
            name: 'PACKAGE', defaultValue: true,
            description: 'Armar y archivar los .zip de servidor y cliente.')
        booleanParam(
            name: 'INSTALLERS', defaultValue: true,
            description: 'Compilar los instaladores de Windows (suite completa y cliente) con Inno Setup 6, si el agente lo tiene.')
    }

    environment {
        DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
        DOTNET_NOLOGO                     = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

        SOLUTION     = 'CLRTrueCentralVMS.slnx'
        RUNTIME_ID   = 'win-x64'
        DIST         = 'dist'
        // Binarios de terceros del nodo. Cámbielo en la configuración del job
        // si en su agente viven en otra ruta.
        BINARIES_DIR = 'C:\\CI\\truecentral-binaries'
    }

    stages {

        stage('Checkout') {
            steps {
                checkout scm
                powershell 'git --no-pager log -1 --pretty="%h %ad %s" --date=short'
            }
        }

        // La versión del producto vive en el archivo VERSION (Directory.Build.props
        // la propaga a TODOS los binarios). El número de build entra solo en la
        // versión de archivo, para distinguir dos compilaciones de la misma
        // versión sin mentirle a /api/health ni al título del cliente.
        stage('Versión') {
            steps {
                script {
                    env.SEMVER       = readFile('VERSION').trim()
                    env.FILE_VERSION = "${env.SEMVER}.${env.BUILD_NUMBER}"
                    env.COMMIT       = (env.GIT_COMMIT ?: 'sin-commit').take(7)
                    // El SDK ya le pega el commit completo a InformationalVersion
                    // (IncludeSourceRevisionInInformationalVersion), así que acá
                    // solo va el número de build: queda "0.1.0+42.<sha>".
                    env.INFO_VERSION = "${env.SEMVER}+${env.BUILD_NUMBER}"

                    currentBuild.displayName = "#${env.BUILD_NUMBER} — ${env.SEMVER}"
                    currentBuild.description = env.INFO_VERSION
                    echo "Versión del producto: ${env.SEMVER} (versión de archivo ${env.FILE_VERSION})"
                }
            }
        }

        // Gate de higiene: los secretos de runtime y los datos de PostgreSQL
        // están en .gitignore, pero un `git add -f` los metería igual. Si algo
        // de eso quedó versionado, el build para acá.
        stage('Higiene de secretos') {
            steps {
                powershell '''
                    $filtrado = git ls-files -- "*.key" "*.secret" "pgdata/*" "mediamtx.runtime.yml" "admin-initial.txt" "appsettings.Local.json"
                    if ($filtrado) {
                        Write-Host "Archivos con secretos o datos de runtime versionados:" -ForegroundColor Red
                        $filtrado | ForEach-Object { Write-Host "  $_" }
                        Write-Host "Saquelos del indice (git rm --cached) antes de publicar."
                        exit 1
                    }
                    Write-Host "OK: no hay secretos ni pgdata versionados."
                '''
            }
        }

        // native\ y tools\ no viven en git. Se copian del nodo; si faltan, el
        // build sigue (compila igual) pero el paquete queda sin ellos.
        stage('Binarios de terceros') {
            steps {
                script {
                    if (fileExists(env.BINARIES_DIR)) {
                        powershell label: 'Copiar native y tools', script: '''
                            $ErrorActionPreference = "Stop"
                            foreach ($carpeta in "native", "tools") {
                                $origen = Join-Path $env:BINARIES_DIR $carpeta
                                if (-not (Test-Path $origen)) {
                                    Write-Warning ("{0}: no existe en {1} (omitido)." -f $carpeta, $env:BINARIES_DIR)
                                    continue
                                }
                                # /MIR deja el destino identico al origen; robocopy
                                # usa 0-7 para exito y 8+ para error real.
                                robocopy $origen (Join-Path $env:WORKSPACE $carpeta) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
                                if ($LASTEXITCODE -ge 8) { throw ("{0}: robocopy fallo con codigo {1}." -f $carpeta, $LASTEXITCODE) }
                                # Y hay que blanquearlo: un 1 (copio archivos) es exito para
                                # robocopy, pero Jenkins toma el ultimo codigo de salida del
                                # bloque como el del paso y daria la etapa por fallida.
                                $global:LASTEXITCODE = 0
                                $mb = [math]::Round((Get-ChildItem $carpeta -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
                                Write-Host ("  OK  {0} ({1} MB)" -f $carpeta, $mb) -ForegroundColor Green
                            }
                        '''
                        env.BINARIES_OK = fileExists('native/hikvision/HCNetSDK.dll') ? 'true' : 'false'
                    } else {
                        env.BINARIES_OK = 'false'
                    }

                    if (env.BINARIES_OK != 'true') {
                        echo "AVISO: no se encontraron los binarios de terceros en ${env.BINARIES_DIR}. " +
                             'El servidor se compilará SIN las DLL nativas de Hikvision/Dahua.'
                    }
                }
            }
        }

        // Restaurar y compilar sin RID: es el gate rápido de errores. La
        // restauración específica de win-x64 la hace después cada publish.
        stage('Restaurar') {
            steps {
                dotnet('dotnet restore', "restore \"${env.SOLUTION}\"")
            }
        }

        stage('Compilar') {
            steps {
                dotnet('dotnet build', "build \"${env.SOLUTION}\" -c Release --no-restore " +
                       "-p:Version=${env.SEMVER} -p:FileVersion=${env.FILE_VERSION} " +
                       "-p:InformationalVersion=${env.INFO_VERSION} " +
                       '-consoleLoggerParameters:Summary')
            }
        }

        // Gate de vulnerabilidades conocidas en dependencias (ISO 27001 8.8),
        // equivalente al pip-audit de cortex/vgt. Dos detalles de `dotnet list
        // package`: devuelve 0 aunque encuentre problemas, y su texto viene
        // traducido al idioma del agente — por eso se lee el JSON y no la
        // salida humana (un `-match "vulnerable"` daría falso positivo con el
        // mensaje "no tiene paquetes vulnerables").
        stage('Auditoría de dependencias') {
            when { expression { return params.RUN_AUDIT } }
            steps {
                powershell '''
                    $ErrorActionPreference = "Stop"
                    $hallazgos = @()

                    Get-ChildItem src -Recurse -Filter *.csproj | ForEach-Object {
                        $nombre = $_.Name
                        Write-Host ("--- {0}" -f $nombre) -ForegroundColor Cyan

                        $json = & dotnet list $_.FullName package --vulnerable --include-transitive --format json --output-version 1
                        if ($LASTEXITCODE -ne 0) {
                            $json | Write-Host
                            throw ("dotnet list package fallo en {0} (el agente no llega a nuget.org?)." -f $nombre)
                        }

                        $datos = ($json -join "`n") | ConvertFrom-Json
                        foreach ($proyecto in $datos.projects) {
                            # Sin "frameworks" = sin paquetes vulnerables.
                            if (-not $proyecto.frameworks) { continue }
                            foreach ($marco in $proyecto.frameworks) {
                                $paquetes = @()
                                if ($marco.topLevelPackages)   { $paquetes += $marco.topLevelPackages }
                                if ($marco.transitivePackages) { $paquetes += $marco.transitivePackages }
                                foreach ($paquete in $paquetes) {
                                    foreach ($falla in $paquete.vulnerabilities) {
                                        $hallazgos += ("{0}: {1} {2} - severidad {3} - {4}" -f `
                                            $nombre, $paquete.id, $paquete.resolvedVersion, $falla.severity, $falla.advisoryurl)
                                    }
                                }
                            }
                        }
                    }

                    if ($hallazgos) {
                        Write-Host "Paquetes con vulnerabilidades conocidas:" -ForegroundColor Red
                        $hallazgos | ForEach-Object { Write-Host ("  {0}" -f $_) }
                        Write-Host "Actualicelos antes de publicar."
                        exit 1
                    }
                    Write-Host "OK: sin paquetes vulnerables." -ForegroundColor Green
                '''
            }
        }

        // Todavía no hay proyectos de prueba en la solución. La etapa los toma
        // sola en cuanto exista el primer *.Tests.csproj. Para que Jenkins
        // muestre el detalle de cada prueba (y no solo el .trx archivado),
        // agregue el paquete JunitXml.TestLogger al proyecto de pruebas y
        // sume el logger "junit;LogFilePath=reports\\junit.xml".
        stage('Pruebas') {
            steps {
                script {
                    def proyectos = powershell(returnStdout: true, script:
                        '(Get-ChildItem src -Recurse -Filter *.Tests.csproj | Measure-Object).Count').trim()

                    if (proyectos == '0') {
                        echo 'Sin proyectos de prueba (*.Tests.csproj): etapa omitida.'
                    } else {
                        dotnet('dotnet test', "test \"${env.SOLUTION}\" -c Release --no-build " +
                               '--logger "trx;LogFileName=resultados.trx" --results-directory reports')
                    }
                }
            }
        }

        // Se publica proyecto por proyecto (no en paralelo: Server y Client
        // comparten Core y dos MSBuild simultáneos se pisan en obj\).
        // La publicación reconstruye con el RID; la etapa Compilar es el gate
        // rápido de errores, por eso no se usa --no-build acá.
        stage('Publicar') {
            steps {
                powershell "Remove-Item -Recurse -Force '${env.DIST}' -ErrorAction SilentlyContinue"

                script {
                    def comunes = "-c Release -r ${env.RUNTIME_ID} " +
                                  "--self-contained ${params.SELF_CONTAINED} " +
                                  "-p:Version=${env.SEMVER} -p:FileVersion=${env.FILE_VERSION} " +
                                  "-p:InformationalVersion=${env.INFO_VERSION}"

                    dotnet('Publicar servidor',
                        'publish src\\TrueCentralVms.Server\\TrueCentralVms.Server.csproj ' +
                        "${comunes} -o \"${env.DIST}\\server\"")

                    // Watchdog: monitor de escritorio del servidor. Va DENTRO del
                    // paquete del servidor, en la misma subcarpeta que usa el
                    // instalador ({app}\watchdog), para que el .zip quede igual
                    // a una instalacion.
                    dotnet('Publicar watchdog',
                        'publish src\\TrueCentralVms.Watchdog\\TrueCentralVms.Watchdog.csproj ' +
                        "${comunes} -o \"${env.DIST}\\server\\watchdog\"")

                    dotnet('Publicar cliente',
                        'publish src\\TrueCentralVms.Client\\TrueCentralVms.Client.csproj ' +
                        "${comunes} -o \"${env.DIST}\\client\"")
                }
            }
        }

        // Las herramientas se buscan en runtime como tools\ junto al ejecutable
        // (EmbeddedPostgres, MediaMtxManager) y el cliente busca su FFmpeg en la
        // carpeta FFmpeg\ junto al exe (App.xaml.cs). El paquete respeta esas
        // rutas para que el .zip funcione tal cual se descomprime.
        stage('Empaquetar') {
            when { expression { return params.PACKAGE } }
            steps {
                powershell '''
                    $ErrorActionPreference = "Stop"

                    function Copiar-Arbol($origen, $destino, $etiqueta) {
                        if (-not (Test-Path $origen)) {
                            Write-Warning ("{0}: falta '{1}' (el paquete queda sin esto)." -f $etiqueta, $origen)
                            return
                        }
                        New-Item -ItemType Directory -Force $destino | Out-Null
                        robocopy $origen $destino /E /NFL /NDL /NJH /NJS /NP | Out-Null
                        if ($LASTEXITCODE -ge 8) { throw ("{0}: robocopy fallo con codigo {1}." -f $etiqueta, $LASTEXITCODE) }
                        # Ver la nota de la etapa "Binarios de terceros": un codigo 1 de
                        # robocopy es exito, pero se lo llevaria Jenkins como fallo del paso.
                        $global:LASTEXITCODE = 0
                        Write-Host ("  OK  {0}" -f $etiqueta) -ForegroundColor Green
                    }

                    # Servidor: base embebida, media server y el ffmpeg del proxy de canales.
                    Copiar-Arbol "tools\\postgres" "$env:DIST\\server\\tools\\postgres" "PostgreSQL embebido"
                    Copiar-Arbol "tools\\mediamtx" "$env:DIST\\server\\tools\\mediamtx" "MediaMTX"
                    Copiar-Arbol "tools\\ffmpeg"   "$env:DIST\\server\\tools\\ffmpeg"   "FFmpeg (proxy de canales)"

                    # Cliente: FlyleafLib exige el FFmpeg compartido de SU release.
                    Copiar-Arbol "tools\\ffmpeg-flyleaf" "$env:DIST\\client\\FFmpeg" "FFmpeg (Flyleaf)"

                    # tar.exe de Windows comprime cientos de MB en una fraccion
                    # de lo que tarda Compress-Archive.
                    $servidor = "CLRTrueCentralVMS-Server-$env:SEMVER.zip"
                    $cliente  = "CLRTrueCentralVMS-Client-$env:SEMVER.zip"

                    tar.exe -a -c -f "$env:DIST\\$servidor" -C "$env:DIST" server
                    if ($LASTEXITCODE -ne 0) { throw "tar fallo al comprimir el servidor." }
                    tar.exe -a -c -f "$env:DIST\\$cliente" -C "$env:DIST" client
                    if ($LASTEXITCODE -ne 0) { throw "tar fallo al comprimir el cliente." }

                    Get-ChildItem "$env:DIST\\*.zip" |
                        ForEach-Object { "{0}  {1} MB" -f $_.Name, [math]::Round($_.Length / 1MB, 1) } |
                        Write-Host
                '''

                script {
                    if (env.BINARIES_OK != 'true') {
                        unstable('Paquetes armados SIN los binarios de terceros: no son instalables tal cual.')
                    }
                }
            }
        }

        // Instaladores de Windows (Inno Setup 6): la suite completa y el
        // cliente solo. Solo si el agente tiene ISCC.exe y los binarios de
        // terceros; si no, se avisa y quedan los .zip de la etapa anterior.
        stage('Instaladores') {
            when { expression { return params.INSTALLERS } }
            steps {
                script {
                    def iscc = powershell(returnStdout: true, script: '''
                        $candidatos = @(
                            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\\ISCC.exe"),
                            (Join-Path $env:ProgramFiles "Inno Setup 6\\ISCC.exe"),
                            (Join-Path $env:LOCALAPPDATA "Programs\\Inno Setup 6\\ISCC.exe"))
                        ($candidatos | Where-Object { Test-Path $_ } | Select-Object -First 1)
                    ''').trim()

                    if (!iscc) {
                        echo 'AVISO: Inno Setup 6 no está en el agente: no se generan instaladores ' +
                             '(winget install -e --id JRSoftware.InnoSetup).'
                    } else if (env.BINARIES_OK != 'true') {
                        echo 'AVISO: sin los binarios de terceros no se generan instaladores.'
                    } else {
                        // El script vuelve a publicar en build\publish (self-contained,
                        // como exigen los .iss) y deja los .exe en dist\.
                        powershell label: 'installer\\build-installers.ps1', script: '''
                            powershell -NoProfile -ExecutionPolicy Bypass -File installer\\build-installers.ps1 -Version $env:SEMVER
                            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                        '''
                    }
                }
            }
        }

    }

    post {
        always {
            junit allowEmptyResults: true, testResults: 'reports/**/*.xml'
            archiveArtifacts artifacts: 'reports/**/*.trx', allowEmptyArchive: true
        }
        success {
            archiveArtifacts artifacts: 'dist/*.zip, dist/*.exe', allowEmptyArchive: true, fingerprint: true
        }
        unstable {
            archiveArtifacts artifacts: 'dist/*.zip, dist/*.exe', allowEmptyArchive: true, fingerprint: true
        }
        cleanup {
            // El workspace NO se limpia a propósito: conservar obj\ y los
            // binarios de terceros ahorra varios minutos por build. Solo se
            // borra lo publicado, que ya quedó archivado.
            powershell "Remove-Item -Recurse -Force '${env.DIST}' -ErrorAction SilentlyContinue"
        }
    }
}
