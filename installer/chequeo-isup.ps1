<#
.SYNOPSIS
    Chequeo previo a actualizar TrueCentral a la versión con ISUP propio (sin el Hik IP Receiver Pro). SOLO LECTURA.

.DESCRIPTION
    Se ejecuta en el servidor de producción ANTES de actualizar, con la versión 0.5.x todavía en marcha. Lee por la API del
    servidor los paneles de alarma que reportan a través de la receptora de Hikvision (driver hikvision-iprp) y el inventario
    de la receptora, y dice panel por panel si al actualizar se reconectará solo al servicio ISUP propio o qué habrá que hacer.
    Reproduce exactamente lo que hará la migración IsupConnections con cada panel. Si corre en el propio servidor, revisa además
    la receptora instalada, los puertos 7660-7667 y 7699, y la sección Isup de appsettings.Local.json.

    No modifica nada: solo consulta. El inicio y el cierre de la sesión quedan en la bitácora, como cualquier ingreso.
    Nunca muestra ni guarda claves: de cada panel solo informa si el servidor tiene su clave ISUP guardada.

.PARAMETER Servidor
    URL del panel web (por omisión http://localhost:5090).

.PARAMETER Usuario
    Usuario con el permiso «Paneles de alarma» (por omisión admin). La contraseña se pide al ejecutar.

.PARAMETER Credencial
    Usuario y contraseña ya armados (Get-Credential). Sin este parámetro la contraseña se pide al ejecutar.

.PARAMETER Salida
    Carpeta donde se guardan el informe (.txt) y la tabla por panel (.csv). Por omisión, la carpeta actual.

.PARAMETER CarpetaServidor
    Carpeta donde está instalado el servidor, si no se puede deducir del servicio CLRTrueCentralVMS.

.PARAMETER ReceptoraLocal
    Dirección de la receptora instalada junto al servidor (por omisión 127.0.0.1:8091, la que dejaba el instalador).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\chequeo-isup.ps1 -Usuario admin
#>
param(
    [string] $Servidor = 'http://localhost:5090',
    [string] $Usuario = 'admin',
    [pscredential] $Credencial,
    [string] $Salida = (Get-Location).Path,
    [string] $ReceptoraLocal = '127.0.0.1:8091',
    [string] $CarpetaServidor
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Servidor = $Servidor.TrimEnd('/')
$informe = New-Object System.Collections.Generic.List[string]
function Linea([string] $texto = '', [string] $color = '') {
    $informe.Add($texto)
    if ($color) { Write-Host $texto -ForegroundColor $color } else { Write-Host $texto }
}

# ---------------------------------------------------------------------------
# API (solo GET y el listado de la receptora, que también es de lectura)
# ---------------------------------------------------------------------------
$script:token = $null
function Api([string] $metodo, [string] $ruta, $cuerpo = $null) {
    $pedido = @{ Method = $metodo; Uri = "$Servidor$ruta"; ContentType = 'application/json; charset=utf-8'; TimeoutSec = 90 }
    if ($script:token) { $pedido.Headers = @{ Authorization = "Bearer $script:token" } }
    if ($null -ne $cuerpo) { $pedido.Body = [Text.Encoding]::UTF8.GetBytes(($cuerpo | ConvertTo-Json -Depth 5)) }
    try {
        # PowerShell 5.1 entrega un arreglo JSON como UN objeto: devolverlo desde una variable lo desenrolla en sus elementos.
        $respuesta = Invoke-RestMethod @pedido
        return $respuesta
    } catch {
        $detalle = $_.ErrorDetails.Message
        if (-not $detalle -and $_.Exception.Response) {
            try { $detalle = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch { }
        }
        try { $j = $detalle | ConvertFrom-Json; if ($j.error) { $detalle = $j.error } } catch { }
        throw "$metodo $ruta -> $($_.Exception.Message) $detalle"
    }
}

if ($Credencial) { $Usuario = $Credencial.UserName; $clave = $Credencial.Password }
else { $clave = Read-Host "Contraseña de '$Usuario' en $Servidor" -AsSecureString }
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave)
try {
    $plano = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $login = Api 'POST' '/api/auth/login' @{ username = $Usuario; password = $plano }
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    $plano = $null
}
$script:token = $login.token
if (-not $script:token) { throw 'El servidor no entregó una sesión.' }

try {
    $ahora = Get-Date
    Linea "Chequeo previo al ISUP propio — $Servidor — $($ahora.ToString('dd-MM-yyyy HH:mm'))" 'Cyan'
    Linea 'Solo lectura: no se modificó nada.'
    Linea ''

    $paneles = @(Api 'GET' '/api/alarms/panels')
    $porReceptora = @($paneles | Where-Object { $_.driverKey -eq 'hikvision-iprp' })
    $directos = @($paneles | Where-Object { $_.driverKey -ne 'hikvision-iprp' })

    # -----------------------------------------------------------------------
    # Inventario de cada receptora (estado e ID ISUP real de cada equipo)
    # -----------------------------------------------------------------------
    $inventario = @{}   # "host:port" -> lista de equipos, o el texto del error
    $grupos = @{}
    foreach ($p in $porReceptora) { $k = "$($p.host):$($p.port)"; if (-not $grupos.ContainsKey($k)) { $grupos[$k] = $p } }
    if (-not $grupos.ContainsKey($ReceptoraLocal)) { $grupos[$ReceptoraLocal] = $null }
    foreach ($k in $grupos.Keys) {
        $primero = $grupos[$k]
        $hp = $k.Split(':')
        $cuerpo = if ($k -eq $ReceptoraLocal) {
            @{ host = $hp[0]; port = [int]$hp[1]; useHttps = $false; username = ''; password = $null }
        } else {
            @{ host = ''; port = 0; useHttps = $false; username = ''; password = $null; panelId = $primero.id }
        }
        try { $inventario[$k] = @(Api 'POST' '/api/alarms/receiver/devices/list' $cuerpo) }
        catch { $inventario[$k] = "no se pudo consultar: $_" }
    }

    function BuscarEquipo([string] $receptora, [string] $id) {
        $lista = $inventario[$receptora]
        if (-not $id -or -not ($lista -is [array])) { return $null }
        return $lista | Where-Object { $_.devIndex -eq $id -or ($_.isupId -and $_.isupId -eq $id) } | Select-Object -First 1
    }

    # -----------------------------------------------------------------------
    # Lo que hará la migración IsupConnections con cada panel (mismo criterio que su SQL)
    # -----------------------------------------------------------------------
    $uuid = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-'
    $tomados = @{}
    $filas = New-Object System.Collections.Generic.List[object]
    foreach ($p in ($porReceptora | Sort-Object id)) {
        $receptora = "$($p.host):$($p.port)"
        $esLocal = $receptora -eq $ReceptoraLocal
        $equipo = BuscarEquipo $receptora $p.deviceId
        $idNuevo = if ($p.deviceId -and $p.deviceId.Length -le 32 -and $p.deviceId -notmatch $uuid) { $p.deviceId } else { $null }
        $repetido = $null
        # En PostgreSQL la comparación de texto distingue mayúsculas: igual que aquí con ContainsKey sobre el texto tal cual.
        if ($idNuevo -and $tomados.ContainsKey($idNuevo)) { $repetido = $tomados[$idNuevo]; $idNuevo = $null }
        elseif ($idNuevo) { $tomados[$idNuevo] = $p.name }

        $hallazgos = New-Object System.Collections.Generic.List[string]
        $veredicto = 'Se reconecta solo'
        if (-not $idNuevo) {
            $veredicto = 'No se conectará'
            if ($repetido) { $hallazgos.Add("ID ISUP repetido con «$repetido»: quedará sin ID y deshabilitado.") }
            elseif ($equipo -and $equipo.isupId) { $hallazgos.Add("Quedará sin ID y deshabilitado (guardaba el uuid de la receptora). Según la receptora su ID ISUP es «$($equipo.isupId)»: escríbalo al editar el panel después de actualizar.") }
            else { $hallazgos.Add('Quedará sin ID ISUP y deshabilitado: habrá que escribir su ID y su clave ISUP después de actualizar.') }
        }
        if ($p.deviceProtocol -eq 'otap') {
            $veredicto = 'No se conectará'
            $hallazgos.Add('Reporta por OTAP: el servicio ISUP propio no atiende OTAP. Cambie el panel a ISUP 5.0 hacia este servidor.')
        }
        if (-not $p.hasDeviceKey -and $p.deviceProtocol -ne 'otap') {
            if ($veredicto -eq 'Se reconecta solo') { $veredicto = 'Requiere acción' }
            $hallazgos.Add('El servidor no tiene guardada su clave ISUP (panel adoptado sin clave): será rechazado hasta escribirla en su formulario. Anote la clave del panel antes de actualizar.')
        }
        if (-not $esLocal) {
            if ($veredicto -eq 'Se reconecta solo') { $veredicto = 'Requiere acción' }
            $hallazgos.Add("Reporta a una receptora de otro equipo ($receptora): seguirá registrándose allá. Para pasarlo a este servidor hay que cambiar en el panel la plataforma ISUP a la dirección de este servidor, puerto 7660.")
        }
        if ($idNuevo -and $equipo -and $equipo.isupId -and $equipo.isupId -ne $idNuevo) {
            if ($veredicto -eq 'Se reconecta solo') { $veredicto = 'Requiere acción' }
            $hallazgos.Add("El ID guardado («$idNuevo») no coincide con el que informa la receptora («$($equipo.isupId)»).")
        }
        if (-not $equipo -and -not ($inventario[$receptora] -is [string])) {
            $hallazgos.Add('No figura en la receptora (o no se pudo ubicar por su ID).')
        }
        if ($p.status -ne 'Online') {
            $motivo = if ($p.lastError) { ": $($p.lastError)" } else { '' }
            $hallazgos.Add("Hoy no está en línea ($($p.status)$motivo).")
        }
        if (-not $p.enabled) { $hallazgos.Add('Está pausado en el VMS (monitoreo desactivado).') }

        $filas.Add([pscustomobject]@{
            Id = $p.id
            Panel = $p.name
            Receptora = $receptora
            IdGuardado = $p.deviceId
            IdIsupTrasMigrar = $idNuevo
            IdSegunReceptora = if ($equipo) { $equipo.isupId } else { $null }
            Protocolo = if ($p.deviceProtocol) { $p.deviceProtocol } else { 'isup' }
            ClaveGuardada = [bool]$p.hasDeviceKey
            EstadoVms = $p.status
            EstadoEnReceptora = if ($equipo) { $equipo.status } else { $null }
            Veredicto = $veredicto
            Detalle = ($hallazgos -join ' ')
        })
    }

    # -----------------------------------------------------------------------
    # Informe
    # -----------------------------------------------------------------------
    Linea "Paneles de alarma: $($paneles.Count) en total; $($porReceptora.Count) por la receptora de Hikvision; $($directos.Count) por conexión directa (no cambian)." 'Cyan'
    Linea ''
    foreach ($f in $filas) {
        $color = switch ($f.Veredicto) { 'Se reconecta solo' { 'Green' } 'Requiere acción' { 'Yellow' } default { 'Red' } }
        Linea "[$($f.Veredicto)] #$($f.Id) $($f.Panel) — ID $(if ($f.IdIsupTrasMigrar) { $f.IdIsupTrasMigrar } else { '(ninguno)' }), $($f.Protocolo), clave $(if ($f.ClaveGuardada) { 'guardada' } else { 'NO guardada' }), receptora $($f.Receptora)" $color
        if ($f.Detalle) { Linea "    $($f.Detalle)" }
    }
    if ($filas.Count -eq 0) { Linea 'No hay paneles por la receptora: la actualización no afecta a ningún panel.' 'Green' }
    Linea ''

    $ok = @($filas | Where-Object Veredicto -eq 'Se reconecta solo').Count
    $accion = @($filas | Where-Object Veredicto -eq 'Requiere acción').Count
    $no = @($filas | Where-Object Veredicto -eq 'No se conectará').Count
    Linea "Resumen: $ok se reconectan solos, $accion requieren acción, $no no se conectarán sin intervenir." 'Cyan'
    Linea ''

    foreach ($k in $inventario.Keys) {
        if ($k -ne $ReceptoraLocal -and $inventario[$k] -is [string]) { Linea "Receptora $k`: $($inventario[$k])" 'Yellow' }
    }

    # Equipos que viven solo en la receptora local: se pierden al retirarla.
    $local = $inventario[$ReceptoraLocal]
    if ($local -is [string]) {
        Linea "Receptora local ($ReceptoraLocal): $local" 'Yellow'
    } elseif ($local) {
        $usados = @($porReceptora | Where-Object { "$($_.host):$($_.port)" -eq $ReceptoraLocal } | ForEach-Object { $_.deviceId })
        $huerfanos = @($local | Where-Object { $usados -notcontains $_.devIndex -and (-not $_.isupId -or $usados -notcontains $_.isupId) })
        Linea "Receptora local ($ReceptoraLocal): $($local.Count) equipo(s) registrados."
        foreach ($h in $huerfanos) {
            Linea "    Sin panel en el VMS: «$($h.name)» ID $($h.isupId) ($($h.status)). Se borra con la receptora: si se usa, agréguelo como panel ISUP después de actualizar." 'Yellow'
        }
    }
    Linea ''

    # -----------------------------------------------------------------------
    # El propio servidor (solo si el script corre en él)
    # -----------------------------------------------------------------------
    $urlLocal = $Servidor -match ('^https?://(localhost|127\.0\.0\.1|\[::1\]|' + [regex]::Escape($env:COMPUTERNAME) + ')(:|/|$)')
    $carpeta = $CarpetaServidor
    if (-not $carpeta -and $urlLocal) {
        $servicio = Get-CimInstance Win32_Service -Filter "Name='CLRTrueCentralVMS'" -ErrorAction SilentlyContinue
        if ($servicio) { $carpeta = Split-Path ($servicio.PathName -replace '^"([^"]+)".*$', '$1') -Parent }
    }
    if ($carpeta) {
        Linea "Este equipo es el servidor ($carpeta):" 'Cyan'
        $exe = Join-Path $carpeta 'TrueCentralVms.Server.exe'
        if (Test-Path $exe) { Linea "    Versión instalada: $((Get-Item $exe).VersionInfo.ProductVersion)" }

        $iprp = Get-CimInstance Win32_Service -Filter "Name='DeviceGatewayService'" -ErrorAction SilentlyContinue
        $secreto = Join-Path $env:ProgramData 'CLRTrueCentralVMS\pgdata\tcvms-iprp.secret'
        if (-not $iprp) {
            Linea '    No hay Hik IP Receiver Pro instalado.'
        } elseif (Test-Path $secreto) {
            Linea "    Hik IP Receiver Pro ($($iprp.State)): lo instaló TrueCentral, el instalador nuevo lo desinstala (con su historial y sus equipos)." 'Green'
        } else {
            Linea "    Hik IP Receiver Pro ($($iprp.State)) que NO instaló TrueCentral: el instalador no lo toca y ocupa los puertos del servicio ISUP. Desinstálelo o detenga DeviceGatewayService antes de actualizar." 'Red'
        }

        # Puertos del servicio ISUP: hoy deberían estar libres o en manos de la receptora.
        $procesos = @{}
        Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $procesos[$_.Id] = $_ }
        $deIprp = { param($id) $pr = $procesos[[int]$id]; $pr -and $pr.Path -and $pr.Path -like '*Hik IP Receiver Pro*' }
        $ocupados = @()
        $ocupados += Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in ((7660..7667) + 7699) } |
            ForEach-Object { [pscustomobject]@{ Puerto = "TCP $($_.LocalPort)"; Pid = $_.OwningProcess } }
        $ocupados += Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 7660, 7663 } |
            ForEach-Object { [pscustomobject]@{ Puerto = "UDP $($_.LocalPort)"; Pid = $_.OwningProcess } }
        $ajenos = @($ocupados | Where-Object { -not (& $deIprp $_.Pid) } | Sort-Object Puerto -Unique)
        if ($ajenos.Count -eq 0) {
            Linea '    Puertos 7660-7667 y 7699: libres o en uso por la receptora (quedan libres al retirarla).' 'Green'
        } else {
            foreach ($o in $ajenos) {
                $pr = $procesos[[int]$o.Pid]
                Linea "    $($o.Puerto) ocupado por $(if ($pr) { "$($pr.ProcessName) (PID $($o.Pid))" } else { "PID $($o.Pid)" }): el servicio ISUP no podrá abrirlo." 'Red'
            }
        }

        $archivoLocal = Join-Path $carpeta 'appsettings.Local.json'
        $publica = $null
        if (Test-Path $archivoLocal) {
            try { $publica = (Get-Content $archivoLocal -Raw -Encoding UTF8 | ConvertFrom-Json).Isup.PublicAddress } catch { Linea "    appsettings.Local.json no se pudo leer: $_" 'Yellow' }
        }
        if ($publica) {
            Linea "    Isup:PublicAddress = $publica"
        } else {
            Linea '    Isup:PublicAddress no está definido: se anunciará la IP local del servidor. Si algún panel se conecta desde fuera de esta red (internet, 4G), defina la IP pública en appsettings.Local.json y reenvíe los puertos 7660-7667 al servidor; si no, se registra pero no envía eventos.' 'Yellow'
        }
        Linea ''
    } else {
        Linea "No se revisó el propio servidor (la URL no es local o no se encontró el servicio CLRTrueCentralVMS): ejecútelo en el servidor, o indique -CarpetaServidor, para revisar la receptora instalada, los puertos y appsettings.Local.json." 'Yellow'
        Linea ''
    }

    Linea 'Antes de actualizar: respalde la base de datos (la migración no tiene vuelta atrás con la versión anterior) y anote las claves ISUP de los paneles marcados «clave NO guardada».'

    # -----------------------------------------------------------------------
    # Archivos
    # -----------------------------------------------------------------------
    if (-not (Test-Path $Salida)) { New-Item -ItemType Directory -Force $Salida | Out-Null }
    $base = Join-Path $Salida ("chequeo-isup-" + $ahora.ToString('yyyyMMdd-HHmm'))
    [IO.File]::WriteAllLines("$base.txt", $informe, (New-Object Text.UTF8Encoding $true))
    $filas | Export-Csv -Path "$base.csv" -NoTypeInformation -Encoding UTF8 -Delimiter ';'
    Write-Host ''
    Write-Host "Informe: $base.txt" -ForegroundColor Cyan
    Write-Host "Tabla:   $base.csv" -ForegroundColor Cyan
} finally {
    try { Api 'POST' '/api/auth/logout' | Out-Null } catch { }
}
