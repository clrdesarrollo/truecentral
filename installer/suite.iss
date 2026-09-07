; ============================================================================
; CLR TrueCentral VMS — Instalador de la SUITE COMPLETA (Inno Setup 6)
; ----------------------------------------------------------------------------
; Instala el servidor como servicio de Windows (CLRTrueCentralVMS) con el
; panel web, los drivers, PostgreSQL embebido, MediaMTX y FFmpeg, y —si se
; marca la tarea— también el cliente de escritorio en el mismo equipo (lanza
; en silencio el instalador del cliente, que queda además guardado en
; {app}\client-setup para distribuirlo a los demás puestos).
;
; Pensado para instalaciones nuevas Y para actualizaciones sobre una
; instalación existente:
;   - Detiene el servicio (y un postgres/mediamtx huérfano) antes de copiar.
;   - Los DATOS viven en %ProgramData%\CLRTrueCentralVMS (pgdata, anpr,
;     workflows, logs) y NO se tocan.
;   - Las MIGRACIONES de esquema las aplica el propio servidor al arrancar
;     (EF Core): el instalador arranca el servicio al final y verifica que
;     /api/health responda.
;   - appsettings.Local.json (ajustes del equipo) nunca se sobreescribe.
;   - El primer administrador se crea desde el panel web, SOLO desde la propia
;     máquina (el sistema viene "desactivado de fábrica"): al terminar se
;     ofrece abrir http://localhost:5090.
;
; Compilación:  installer\build-installers.ps1   (o ISCC /DAppVersion=x.y.z suite.iss)
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName        "CLR TrueCentral VMS"
#define Publisher      "CLRobotics"
#define ServiceName    "CLRTrueCentralVMS"
#define ServerExe      "TrueCentralVms.Server.exe"
#define DataDirName    "CLRTrueCentralVMS"
#define WebPort        "5090"
#define ArcPort        "5091"
#define RtspPort       "8654"
; Puerto del PostgreSQL embebido. Es PRIVADO del sistema: escucha solo en
; 127.0.0.1 y nunca se comparte, por eso no usa el 5432 de PostgreSQL ni el
; 25480 del producto videowall. Si estuviera ocupado, el instalador busca el
; primer libre a partir de este.
#define PgPort         "25490"
#define PgPortLast     "25539"
#define ServerPublish  "..\build\publish\server"
#define WatchdogPublish "..\build\publish\watchdog"
#define WatchdogExe    "TrueCentralVms.Watchdog.exe"
#define PgTools        "..\tools\postgres\pgsql"
#define MediaMtxDir    "..\tools\mediamtx"
#define FfmpegDir      "..\tools\ffmpeg"
; Hik IP Receiver Pro: receptora de los paneles de alarma que reportan por
; ISUP/OTAP (AX PRO, AX HYBRID PRO). Es un componente OBLIGATORIO del sistema,
; no un extra: sin ella esos paneles no se pueden conectar, por eso se instala
; siempre y sin preguntar. El VMS habla con ella por HTTP; su interfaz/API
; viene en el 80,
; que aqui se mueve al 8091 y se ata a 127.0.0.1: solo la usa el servidor de
; este mismo equipo, nunca se expone a la red (los paneles no usan ese puerto,
; se registran por 7660-7667/7091/8661).
#define IprpWebPort    "8091"
#define IprpService    "DeviceGatewayService"
#define IprpDir        "{autopf}\Hik IP Receiver Pro"
#define IprpSetup      "..\tools\iprp\HikIpReceiverPro-Setup.exe"
#if FileExists(IprpSetup)
  #define HasIprp
#else
  #error No existe el instalador del Hik IP Receiver Pro (tools\iprp\HikIpReceiverPro-Setup.exe). Es un componente obligatorio de la suite: obtengalo con build\setup-binaries.ps1.
#endif

#define ClientSetupName "CLRTrueCentralVMS-Client-Setup-" + AppVersion + ".exe"
#define ClientSetup    "..\dist\" + ClientSetupName
#if FileExists(ClientSetup)
  #define HasClient
#else
  #pragma warning "No existe " + ClientSetup + ": la suite se compila SIN el cliente de escritorio (compile primero client.iss)."
#endif

[Setup]
AppId={{B3F7D9E1-4C25-4A6B-9E08-2D5C71A8F0B4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\CLR TrueCentral VMS\Server
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=CLRTrueCentralVMS-Suite-Setup-{#AppVersion}
SetupIconFile=assets\truecentral.ico
UninstallDisplayIcon={app}\{#ServerExe}
UninstallDisplayName={#AppName} (servidor)
VersionInfoVersion={#AppVersion}.0
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; El instalador gestiona por sí mismo el servicio y sus procesos hijos:
CloseApplications=no
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo del &Watchdog en el escritorio"
#ifdef HasClient
Name: "installclient"; Description: "Instalar también el &cliente de escritorio en este equipo (recomendado para el puesto principal)"
#endif

[Dirs]
; Datos y registros fuera de Program Files: sobreviven a las actualizaciones.
Name: "{commonappdata}\{#DataDirName}"
Name: "{commonappdata}\{#DataDirName}\logs"

[InstallDelete]
; Componentes que se reemplazan completos en cada versión (sin restos viejos).
Type: filesandordirs; Name: "{app}\tools\postgres\pgsql"
Type: filesandordirs; Name: "{app}\tools\mediamtx"
Type: filesandordirs; Name: "{app}\tools\ffmpeg"
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\HCNetSDKCom"
Type: filesandordirs; Name: "{app}\watchdog"
; Instaladores del cliente de versiones anteriores (se publica el nuevo).
Type: files; Name: "{app}\client-setup\CLRTrueCentralVMS-Client-Setup-*.exe"

[Files]
; Servidor publicado (self-contained). Se excluyen los restos de desarrollo
; que el SDK Web arrastra como contenido (ajustes locales, configuración
; generada de MediaMTX, logs).
Source: "{#ServerPublish}\*"; DestDir: "{app}"; Excludes: "*.pdb,appsettings.Development.json,appsettings.Local.json,mediamtx.runtime.yml,*.log,pgdata\*,anpr\*,workflows\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "config\appsettings.Production.json"; DestDir: "{app}"; Flags: ignoreversion
; Ajustes del equipo: solo la primera vez; las actualizaciones no lo tocan.
Source: "config\appsettings.Local.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
; Watchdog: monitor de escritorio del servidor. Va en su propia carpeta y se
; reemplaza completo en cada versión (pide elevación en su manifiesto).
Source: "{#WatchdogPublish}\*"; DestDir: "{app}\watchdog"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; PostgreSQL embebido (bin\lib\share).
Source: "{#PgTools}\*"; DestDir: "{app}\tools\postgres\pgsql"; Flags: ignoreversion recursesubdirs createallsubdirs
; MediaMTX: solo el binario (la configuración la genera el servidor en cada
; arranque; auto.crt/auto.key no se distribuyen).
Source: "{#MediaMtxDir}\mediamtx.exe"; DestDir: "{app}\tools\mediamtx"; Flags: ignoreversion
Source: "{#MediaMtxDir}\LICENSE"; DestDir: "{app}\tools\mediamtx"; Flags: ignoreversion skipifsourcedoesntexist
; FFmpeg: proxy de canales con SDP inválido y exportación de grabaciones.
Source: "{#FfmpegDir}\bin\ffmpeg.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "{#FfmpegDir}\LICENSE"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion skipifsourcedoesntexist
; Instalador del cliente: se instala en este equipo si se marcó la tarea y
; queda guardado para llevarlo a los demás puestos de operación.
#ifdef HasClient
Source: "{#ClientSetup}"; DestDir: "{app}\client-setup"; Flags: ignoreversion
#endif

; Receptor de paneles de alarma: se ejecuta y se borra (no queda ocupando
; disco), su desinstalacion es independiente desde Programas y caracteristicas.
#ifdef HasIprp
Source: "{#IprpSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall ignoreversion
#endif

[Icons]
Name: "{autoprograms}\CLR TrueCentral VMS\Panel web de CLR TrueCentral VMS"; Filename: "http://localhost:{#WebPort}"
Name: "{autoprograms}\CLR TrueCentral VMS\Watchdog de CLR TrueCentral VMS"; Filename: "{app}\watchdog\{#WatchdogExe}"
Name: "{autoprograms}\CLR TrueCentral VMS\Datos de CLR TrueCentral VMS"; Filename: "{commonappdata}\{#DataDirName}"
Name: "{autodesktop}\Watchdog de CLR TrueCentral VMS"; Filename: "{app}\watchdog\{#WatchdogExe}"; Tasks: desktopicon
#ifdef HasClient
Name: "{autoprograms}\CLR TrueCentral VMS\Instalador del cliente (para otros puestos)"; Filename: "{app}\client-setup"
#endif

[Run]
; Abre el panel: el primer administrador se crea desde el asistente, que
; solo acepta conexiones desde esta misma máquina.
Filename: "http://localhost:{#WebPort}"; Description: "Abrir el panel web (crear el primer administrador)"; Flags: postinstall shellexec skipifsilent nowait
; shellexec (ShellExecuteEx) y no CreateProcess: el Watchdog pide elevación en
; su manifiesto y el instalador no puede lanzarlo de otra manera.
Filename: "{app}\watchdog\{#WatchdogExe}"; Description: "Abrir el Watchdog del servidor"; Flags: postinstall nowait skipifsilent shellexec unchecked

[Code]
// ---------------------------------------------------------------------------
// Utilidades
// ---------------------------------------------------------------------------
function RunHidden(const FileName, Params: string): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode;
end;

function SysTool(const Name: string): string;
begin
  Result := ExpandConstant('{sys}\') + Name;
end;

function PowerShellExe(): string;
begin
  Result := SysTool('WindowsPowerShell\v1.0\powershell.exe');
end;

function DataDir(): string;
begin
  Result := ExpandConstant('{commonappdata}\{#DataDirName}');
end;

function ServiceExists(): Boolean;
begin
  // sc query devuelve 0 para servicios existentes (corriendo o detenidos)
  // y 1060 cuando el servicio no existe.
  Result := RunHidden(SysTool('sc.exe'), 'query {#ServiceName}') = 0;
end;

function ServiceRunning(): Boolean;
begin
  // Los tokens de estado (RUNNING/STOPPED) no se localizan.
  Result := RunHidden(SysTool('cmd.exe'), '/c sc query {#ServiceName} | find "RUNNING"') = 0;
end;

procedure StopServiceAndWait(const Caption: string);
var
  I: Integer;
begin
  if not ServiceExists() then
    exit;
  if Caption <> '' then
    WizardForm.StatusLabel.Caption := Caption;
  RunHidden(SysTool('sc.exe'), 'stop {#ServiceName}');
  // Esperar el apagado ordenado (incluye pg_ctl stop): hasta 90 s.
  for I := 1 to 180 do
  begin
    if not ServiceRunning() then
      break;
    Sleep(500);
  end;
  Sleep(1000);
end;

// Un Watchdog abierto bloquearía su propio .exe durante la copia (y durante
// el borrado al desinstalar). Se cierra antes: es un monitor, no pierde nada.
procedure KillWatchdog();
begin
  RunHidden(SysTool('taskkill.exe'), '/F /IM {#WatchdogExe}');
end;

// Si un cierre abrupto dejó el postgres embebido corriendo (el servidor lo
// reutilizaría, pero sus binarios quedarían bloqueados y la copia fallaría),
// se detiene con el pg_ctl de la instalación anterior.
procedure StopOrphanPostgres();
var
  PgCtl, Cluster: string;
begin
  // Sin tocar WizardForm: esta rutina corre también durante la desinstalación.
  PgCtl := ExpandConstant('{app}\tools\postgres\pgsql\bin\pg_ctl.exe');
  Cluster := DataDir() + '\pgdata';
  if FileExists(PgCtl) and FileExists(Cluster + '\postmaster.pid') then
    RunHidden(PgCtl, 'stop -D "' + Cluster + '" -m fast -w -t 60');
end;

// Un MediaMTX huérfano de ESTA instalación bloquearía su propio .exe durante
// la copia. El del cliente (otra carpeta) o el de otro programa no se tocan.
procedure StopOrphanMediaMtx();
begin
  RunHidden(PowerShellExe(),
    '-NoProfile -NonInteractive -Command "Get-Process mediamtx,ffmpeg -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"');
end;

// ---------------------------------------------------------------------------
// Puerto de PostgreSQL: convivir con otra instancia ya instalada en el equipo
// ---------------------------------------------------------------------------

// ¿Hay algo escuchando en ese puerto TCP? Se busca ":PUERTO " con el espacio de
// la columna (si no, "2549" también daría positivo dentro de "25490") y NO se
// filtra por estado: netstat traduce LISTENING/ESCUCHANDO según el idioma de
// Windows. Se ejecuta después de PrepareToInstall, que ya detuvo nuestro
// servicio y cualquier postgres huérfano: lo que aparezca es de otro programa.
function PortInUse(Port: Integer): Boolean;
begin
  Result := RunHidden(SysTool('cmd.exe'),
    '/c netstat -an -p TCP | find ":' + IntToStr(Port) + ' " > nul') = 0;
end;

// Puerto del clúster ya existente (leído de su postgresql.conf), o -1 si no hay
// clúster. Con datos ya creados el puerto NO se puede cambiar desde aquí: vive
// dentro del propio clúster (y el servidor lo adopta al arrancar).
function ExistingClusterPort(): Integer;
var
  Lines: TArrayOfString;
  I, P, Parsed: Integer;
  Line: string;
begin
  Result := -1;
  if not FileExists(DataDir() + '\pgdata\PG_VERSION') then
    exit;

  Result := {#PgPort}; // hay clúster; si no se puede leer el .conf, el valor por defecto
  if not LoadStringsFromFile(DataDir() + '\pgdata\postgresql.conf', Lines) then
    exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    // Las líneas comentadas (#port = 5432) no empiezan con "port".
    Line := Trim(Lines[I]);
    if (Copy(Line, 1, 4) = 'port') and (Pos('=', Line) > 0) then
    begin
      Line := Trim(Copy(Line, Pos('=', Line) + 1, Length(Line)));
      P := Pos('#', Line);
      if P > 0 then
        Line := Trim(Copy(Line, 1, P - 1));
      Parsed := StrToIntDef(Line, -1);
      if Parsed > 0 then
        Result := Parsed;   // gana la última aparición, como hace PostgreSQL
    end;
  end;
end;

// ¿El appsettings.Local.json que hay tiene ajustes de verdad, o es la plantilla
// vacía que se acaba de copiar? En la plantilla las únicas líneas sin comentar
// son las llaves.
function LocalJsonHasSettings(const FileName: string): Boolean;
var
  Lines: TArrayOfString;
  I: Integer;
  Line: string;
begin
  Result := False;
  if not LoadStringsFromFile(FileName, Lines) then
    exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    if (Line <> '') and (Line <> '{') and (Line <> '}') and (Copy(Line, 1, 2) <> '//') then
    begin
      Result := True;
      exit;
    end;
  end;
end;

procedure WriteLocalPgPort(Port: Integer);
var
  FileName: string;
  Lines: TArrayOfString;
begin
  FileName := ExpandConstant('{app}\appsettings.Local.json');
  // Si el operador ya tenía ajustes propios, se conservan en un .bak antes de
  // reescribir (este archivo nunca lo tocan las actualizaciones).
  if FileExists(FileName) and LocalJsonHasSettings(FileName) then
    FileCopy(FileName, FileName + '.bak', False);

  SetArrayLength(Lines, 11);
  Lines[0]  := '{';
  Lines[1]  := '  // Ajustes propios de ESTE equipo. Tiene prioridad sobre appsettings.json';
  Lines[2]  := '  // y appsettings.Production.json, y las actualizaciones NUNCA lo sobreescriben.';
  Lines[3]  := '  //';
  Lines[4]  := '  // El instalador detectó que el puerto {#PgPort} estaba ocupado en este equipo';
  Lines[5]  := '  // (otra instancia de PostgreSQL u otro programa) y asignó uno libre. La base';
  Lines[6]  := '  // de datos quedó creada en este puerto: no lo cambie a mano.';
  Lines[7]  := '  "Database": {';
  Lines[8]  := '    "PgPort": ' + IntToStr(Port);
  Lines[9]  := '  }';
  Lines[10] := '}';
  SaveStringsToUTF8File(FileName, Lines, False);
end;

// Elige el puerto del PostgreSQL embebido antes de que el servicio arranque por
// primera vez (el clúster se crea escuchando en el puerto configurado).
procedure ConfigurePgPort();
var
  Existing, Port: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Comprobando el puerto de la base de datos...';

  Existing := ExistingClusterPort();
  if Existing > 0 then
  begin
    // Actualización sobre datos ya existentes: el puerto es parte del clúster.
    if PortInUse(Existing) and not WizardSilent() then
      MsgBox('La base de datos de CLR TrueCentral VMS usa el puerto ' + IntToStr(Existing) +
             ', pero otro programa lo está ocupando en este equipo.' #13#10#13#10 +
             'El servidor no podrá iniciarse hasta liberar ese puerto (o detener el otro programa).' #13#10 +
             'El puerto no se cambia automáticamente porque los datos ya existentes fueron creados con él.',
             mbError, MB_OK);
    exit;
  end;

  Port := {#PgPort};
  while (Port <= {#PgPortLast}) and PortInUse(Port) do
    Port := Port + 1;
  if Port = {#PgPort} then
    exit;   // el puerto por defecto está libre: no hace falta tocar nada

  if Port > {#PgPortLast} then
  begin
    if not WizardSilent() then
      MsgBox('No se encontró ningún puerto libre entre {#PgPort} y {#PgPortLast} para la base de datos.' #13#10 +
             'Libere alguno o indique uno en appsettings.Local.json antes de iniciar el servicio.',
             mbError, MB_OK);
    exit;
  end;

  WriteLocalPgPort(Port);
  if not WizardSilent() then
    MsgBox('El puerto {#PgPort} ya está ocupado en este equipo (otra instancia de PostgreSQL u otro programa).' #13#10#13#10 +
           'La base de datos de CLR TrueCentral VMS usará el puerto ' + IntToStr(Port) + '.' #13#10 +
           'Queda anotado en appsettings.Local.json y no se ve afectado por las actualizaciones.',
           mbInformation, MB_OK);
end;

// ---------------------------------------------------------------------------
// Instalación
// ---------------------------------------------------------------------------
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KillWatchdog();
  StopServiceAndWait('Deteniendo el servicio {#ServiceName}...');
  StopOrphanPostgres();
  StopOrphanMediaMtx();
end;

procedure ConfigureService();
var
  BinPath: string;
begin
  BinPath := '"' + ExpandConstant('{app}') + '\{#ServerExe}"';
  if not ServiceExists() then
    RunHidden(SysTool('sc.exe'),
      'create {#ServiceName} binPath= ' + BinPath + ' start= auto obj= LocalSystem DisplayName= "{#AppName} Server"')
  else
    RunHidden(SysTool('sc.exe'), 'config {#ServiceName} binPath= ' + BinPath + ' start= auto');

  RunHidden(SysTool('sc.exe'),
    'description {#ServiceName} "Servidor del VMS CLR TrueCentral: API, panel web, SignalR, PostgreSQL embebido y MediaMTX."');
  // Recuperación a nivel de sistema: si el PROCESO entero muere, el SCM lo
  // relanza (5 s / 15 s / 60 s; el contador se resetea tras un día estable).
  // Las caídas de los servicios internos las cubre el supervisor del propio
  // servidor (panel web: Sistema > Servicios).
  RunHidden(SysTool('sc.exe'),
    'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000');
  RunHidden(SysTool('sc.exe'), 'failureflag {#ServiceName} 1');
end;

procedure DeleteFirewallRules();
begin
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS Server"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS Server (descubrimiento)"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS MediaMTX"');
end;

procedure ConfigureFirewall();
begin
  DeleteFirewallRules();
  // Panel web + API + SignalR (5090) y receptor de alarmas SIA DC-09 (5091).
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS Server" dir=in action=allow protocol=TCP localport={#WebPort},{#ArcPort} profile=any');
  // Respuestas del descubrimiento de equipos: SADP (UDP 37020), WS-Discovery
  // (UDP 3702) y Dahua (UDP 37810). Regla por programa: solo este servidor.
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS Server (descubrimiento)" dir=in action=allow protocol=UDP program="'
    + ExpandConstant('{app}') + '\{#ServerExe}" profile=any');
  // RTSP hacia los espectadores (clientes de escritorio y decodificadores).
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS MediaMTX" dir=in action=allow protocol=TCP localport={#RtspPort} program="'
    + ExpandConstant('{app}') + '\tools\mediamtx\mediamtx.exe" profile=any');
end;

// Solo SYSTEM y Administradores (por SID: vale en cualquier idioma): la base
// guarda credenciales de equipos, la clave del clúster y la llave AES que
// descifra las contraseñas de los dispositivos.
//
// OJO con el árbol: NO se puede aplicar el /grant con (OI)(CI) usando /T. Esos
// flags de herencia solo son válidos en CARPETAS; sobre cada ARCHIVO icacls los
// rechaza y (con /C) sigue de largo, pero el /inheritance:r ya le quitó los
// permisos heredados y el archivo queda SIN ACL, inaccesible hasta para SYSTEM.
// La forma correcta es poner la ACL heredable en la raíz y hacer que el
// contenido vuelva a heredarla.
procedure TightenDataAcl();
var
  Dir: string;
begin
  Dir := DataDir();
  // Dueño = Administradores, para poder reescribir ACLs de archivos que una
  // versión anterior pudo dejar sin permisos (si no, ni el instalador entra).
  RunHidden(SysTool('icacls.exe'), '"' + Dir + '" /setowner *S-1-5-32-544 /T /C /Q');
  RunHidden(SysTool('icacls.exe'),
    '"' + Dir + '" /inheritance:r /grant *S-1-5-18:(OI)(CI)F /grant *S-1-5-32-544:(OI)(CI)F /Q');
  // El contenido existente vuelve a heredar de la raíz.
  RunHidden(SysTool('icacls.exe'), '"' + Dir + '\*" /reset /T /C /Q');
end;

// Arranca el servicio y espera a que la API responda: en ese momento el
// esquema ya fue creado/migrado por el servidor (las migraciones corren antes
// de abrir el puerto HTTP).
procedure StartServiceAndVerify();
var
  I: Integer;
  Healthy, Crashed: Boolean;
begin
  Crashed := False;
  WizardForm.StatusLabel.Caption := 'Iniciando el servicio {#ServiceName}...';
  RunHidden(SysTool('sc.exe'), 'start {#ServiceName}');

  Healthy := False;
  if FileExists(SysTool('curl.exe')) then
  begin
    WizardForm.StatusLabel.Caption := 'Esperando a que el servidor prepare la base de datos y responda...';
    // Primer arranque: initdb + migraciones puede tardar; hasta 120 s.
    // Por 127.0.0.1 y no "localhost": si el servidor quedara atado solo a IPv4,
    // resolver ::1 primero consumiría el --max-time y el chequeo fallaría con
    // el servicio sano.
    for I := 1 to 60 do
    begin
      if RunHidden(SysTool('curl.exe'),
           '--silent --fail --max-time 5 http://127.0.0.1:{#WebPort}/api/health') = 0 then
      begin
        Healthy := True;
        break;
      end;
      // Si el servicio se cayó no tiene sentido seguir esperando dos minutos.
      if not ServiceRunning() then
      begin
        Crashed := True;
        break;
      end;
      Sleep(2000);
    end;
    if Crashed and not WizardSilent() then
      MsgBox('El servicio se instaló, pero se detiene al arrancar.' #13#10#13#10 +
             'Revise el Visor de eventos de Windows (Registro de aplicaciones, origen CLRTrueCentralVMS).' #13#10 +
             'Causas habituales: el puerto {#WebPort} está ocupado por otro programa, o la base de datos' #13#10 +
             'no pudo iniciarse (puerto {#PgPort} o permisos sobre ' + DataDir() + ').',
             mbError, MB_OK)
    else if not Healthy and not WizardSilent() then
      MsgBox('El servicio se instaló y se inició, pero la API todavía no responde en el puerto {#WebPort}.'#13#10 +
             'Revise el estado en el Visor de eventos de Windows o vuelva a abrir http://localhost:{#WebPort} en unos minutos.',
             mbInformation, MB_OK);
  end;
end;

// Cliente de escritorio en el mismo equipo: se instala con SU propio
// instalador (queda como programa aparte, actualizable por separado).
procedure InstallClient();
var
  Setup, Log: string;
  ResultCode: Integer;
begin
#ifdef HasClient
  if not WizardIsTaskSelected('installclient') then
    exit;
  Setup := ExpandConstant('{app}\client-setup\{#ClientSetupName}');
  if not FileExists(Setup) then
    exit;
  Log := DataDir() + '\logs\client-setup.log';
  WizardForm.StatusLabel.Caption := 'Instalando el cliente de escritorio...';
  if not Exec(Setup, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="' + Log + '"', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    if not WizardSilent() then
      MsgBox('El cliente de escritorio no se pudo instalar (código ' + IntToStr(ResultCode) + ').' #13#10 +
             'Puede instalarlo a mano desde ' + Setup + #13#10 + 'Detalle en ' + Log,
             mbError, MB_OK);
#endif
end;

// ---------------------------------------------------------------------------
// Receptor de paneles de alarma (Hik IP Receiver Pro)
//
// Se instala en silencio y se le cambia el puerto de su web/API: viene en el
// 80 escuchando en todas las interfaces, y aqui queda en 127.0.0.1:{#IprpWebPort}.
// El unico cliente de esa API es el servidor del VMS, que corre en esta misma
// maquina; los paneles NO usan ese puerto (se registran por ISUP/OTAP en
// 7660-7667, 7091 y 8661, que si quedan abiertos).
//
// El puerto vive en dos archivos: nginx.conf (el que escucha de verdad) y
// Config.xml (el que muestra la propia receptora). Se cambian los dos, y solo
// si siguen en el valor de fabrica: si alguien ya los ajusto, se respetan.
// ---------------------------------------------------------------------------
#ifdef HasIprp
function IprpDir(): string;
begin
  Result := ExpandConstant('{#IprpDir}');
end;

// Quita espacios y tabuladores: permite comparar directivas de configuracion
// sin depender de como esten alineadas.
function Squeeze(const Value: string): string;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(Value) do
    if (Value[I] <> ' ') and (Value[I] <> #9) then
      Result := Result + Value[I];
end;

function IprpServiceRunning(): Boolean;
begin
  Result := RunHidden(SysTool('cmd.exe'), '/c sc query {#IprpService} | find "RUNNING"') = 0;
end;

procedure StopIprpService();
var
  I: Integer;
begin
  RunHidden(SysTool('sc.exe'), 'stop {#IprpService}');
  for I := 1 to 60 do
  begin
    if not IprpServiceRunning() then
      break;
    Sleep(500);
  end;
end;

// nginx.conf: "listen 80;" -> "listen 127.0.0.1:8091;". Solo se toca la linea
// del puerto por defecto; si ya esta en otro valor, se deja como esta.
function PatchIprpNginx(): Boolean;
var
  Path, Line, Trimmed: string;
  Lines: TArrayOfString;
  I: Integer;
  Changed: Boolean;
begin
  Result := False;
  Path := IprpDir() + '\nginx\conf\nginx.conf';
  if not FileExists(Path) then
    exit;
  if not LoadStringsFromFile(Path, Lines) then
    exit;

  Changed := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    Trimmed := Trim(Line);
    // Comentarios fuera; y se compara sin espacios para no depender de como
    // venga formateada la linea en la version del proveedor.
    if (Copy(Trimmed, 1, 1) <> '#') and (Squeeze(Trimmed) = 'listen80;') then
    begin
      Lines[I] := '        listen       127.0.0.1:{#IprpWebPort};';
      Changed := True;
    end;
  end;

  if not Changed then
  begin
    // Ya estaba en 8091 (reinstalacion) o alguien lo cambio a mano.
    Result := True;
    exit;
  end;
  Result := SaveStringsToFile(Path, Lines, False);
end;

// La receptora sirve en el MISMO puerto su interfaz web (la raiz) y su API
// (/ISAPI y companeros): un solo nginx reparte por ruta. Aqui se corta solo la
// raiz, para que quede como un servicio interno sin cara visible: quien abra la
// direccion en un navegador —incluso sentado frente al servidor— recibe 403,
// mientras el VMS sigue usando la API con normalidad.
//
// Es reversible: antes de tocar nada se guarda nginx.conf.clr-original. Lo
// unico que se pierde es el complemento de video de la propia receptora, que el
// VMS no usa (verifica las alarmas con sus propias camaras).
function HideIprpWebUi(): Boolean;
var
  Path, Backup: string;
  Lines, Output: TArrayOfString;
  I, Count, Written: Integer;
  Blocked: Boolean;
begin
  Result := False;
  Path := IprpDir() + '\nginx\conf\nginx.conf';
  if not FileExists(Path) then
    exit;
  if not LoadStringsFromFile(Path, Lines) then
    exit;

  Count := GetArrayLength(Lines);

  // Reinstalacion: si ya esta cortada, no hay nada que hacer.
  for I := 0 to Count - 1 do
    if Pos('CLR TrueCentral VMS: interfaz web', Lines[I]) > 0 then
    begin
      Result := True;
      exit;
    end;

  SetArrayLength(Output, Count + 2);
  Written := 0;
  Blocked := False;
  for I := 0 to Count - 1 do
  begin
    Output[Written] := Lines[I];
    Written := Written + 1;
    // Solo la raiz: "location / {". Las de la API (/ISAPI, /SDK, /daf...) no
    // coinciden y quedan intactas.
    if (not Blocked) and (Squeeze(Trim(Lines[I])) = 'location/{') then
    begin
      Output[Written] := '			# CLR TrueCentral VMS: interfaz web deshabilitada (la receptora es un servicio interno).';
      Output[Written + 1] := '			return 403;';
      Written := Written + 2;
      Blocked := True;
    end;
  end;

  if not Blocked then
    exit; // formato inesperado: mejor no tocar el archivo

  Backup := IprpDir() + '\nginx\conf\nginx.conf.clr-original';
  if not FileExists(Backup) then
    FileCopy(Path, Backup, False);

  SetArrayLength(Output, Written);
  Result := SaveStringsToFile(Path, Output, False);
end;

// Config.xml: el <Port>/<ExternalPort> del nodo <HTTP> es lo que la receptora
// informa como su puerto. Se cambia el primer par 80 que aparece tras <HTTP>.
function PatchIprpConfigXml(): Boolean;
var
  Path, Trimmed: string;
  Lines: TArrayOfString;
  I: Integer;
  InHttp: Boolean;
begin
  Result := False;
  Path := IprpDir() + '\Config.xml';
  if not FileExists(Path) then
    exit;
  if not LoadStringsFromFile(Path, Lines) then
    exit;

  InHttp := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Trimmed := Trim(Lines[I]);
    if Trimmed = '<HTTP>' then
      InHttp := True
    else if Trimmed = '</HTTP>' then
      InHttp := False
    else if InHttp then
    begin
      if Squeeze(Trimmed) = '<Port>80</Port>' then
        Lines[I] := '            <Port>{#IprpWebPort}</Port>'
      else if Squeeze(Trimmed) = '<ExternalPort>80</ExternalPort>' then
        Lines[I] := '            <ExternalPort>{#IprpWebPort}</ExternalPort>';
    end;
  end;
  Result := SaveStringsToFile(Path, Lines, False);
end;

// Doble candado sobre el puerto de la receptora: aunque su instalador (o una
// actualizacion suya) abra el puerto, una regla de bloqueo gana sobre las de
// permiso. El trafico por loopback no pasa por el firewall, asi que el
// servidor del VMS sigue llegando.
procedure ConfigureIprpFirewall();
begin
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS IP Receiver Pro (solo local)"');
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS IP Receiver Pro (solo local)" dir=in action=block protocol=TCP localport={#IprpWebPort} profile=any');
  // Puertos por los que los paneles se registran y reportan: esos si se abren.
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS paneles ISUP"');
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS paneles ISUP" dir=in action=allow protocol=TCP localport=7091,7660-7667,8661 profile=any');
end;

// ¿Este servidor ya administra la receptora? La marca es su credencial, que
// el servidor genera al activarla (ver LocalIpReceiverService).
function IprpManagedByUs(): Boolean;
begin
  Result := FileExists(DataDir() + '\pgdata\tcvms-iprp.secret');
end;

function IprpServiceExists(): Boolean;
begin
  Result := RunHidden(SysTool('sc.exe'), 'query {#IprpService}') = 0;
end;

// Caso incomodo: en el equipo ya habia una receptora instalada y activada a
// mano, con una contrasena que este servidor no conoce y que el fabricante no
// permite reiniciar en local (solo un Super Admin de Hik-Partner Pro puede).
// Sin esa contrasena el VMS no puede administrarla.
//
// La unica salida es reinstalarla desde cero, y eso BORRA su historial de
// eventos y los equipos que tenga registrados: por eso se pregunta siempre,
// con No por defecto, y nunca se hace en una instalacion silenciosa. Quien
// prefiera conservarla solo tiene que escribir esa contrasena una vez al
// agregar el primer panel.
procedure ResetOrphanIprp();
var
  Uninstaller: string;
  ResultCode, I: Integer;
begin
  if WizardSilent() then
    exit;
  if not IprpServiceExists() then
    exit;      // no hay nada instalado: la instalacion normal la activara sola
  if IprpManagedByUs() then
    exit;      // ya es nuestra: su credencial esta guardada

  Uninstaller := IprpDir() + '\uninst.exe';
  if not FileExists(Uninstaller) then
    exit;

  if MsgBox('En este equipo ya hay un receptor de paneles de alarma instalado y activado,' #13#10 +
            'con una contrasena que este sistema no conoce. El fabricante no permite' #13#10 +
            'reiniciarla localmente.' #13#10#13#10 +
            '¿Desea reinstalarlo desde cero para que el sistema lo administre solo?' #13#10#13#10 +
            'ATENCION: se BORRAN su historial de eventos y los equipos que tenga' #13#10 +
            'registrados; los paneles habra que volver a darlos de alta.' #13#10#13#10 +
            'Si responde No, el receptor se conserva tal cual y bastara con escribir su' #13#10 +
            'contrasena una vez, al agregar el primer panel en el panel web.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    exit;

  WizardForm.StatusLabel.Caption := 'Quitando el receptor de paneles anterior...';
  StopIprpService();
  Exec(Uninstaller, '/S', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // El desinstalador NSIS se lanza y devuelve el control enseguida: hay que
  // esperar a que el servicio desaparezca de verdad antes de reinstalar.
  for I := 1 to 60 do
  begin
    if not IprpServiceExists() then
      break;
    Sleep(1000);
  end;
  Sleep(2000);
  DelTree(IprpDir(), True, True, True);
end;

procedure InstallIprp();
var
  Setup: string;
  ResultCode, I: Integer;
  Healthy: Boolean;
begin
  Setup := ExpandConstant('{tmp}\HikIpReceiverPro-Setup.exe');
  if not FileExists(Setup) then
    exit;

  ResetOrphanIprp();

  WizardForm.StatusLabel.Caption := 'Instalando el receptor de paneles de alarma...';
  // Instalador NSIS: /S es silencioso y /D (sin comillas y al final) fija la carpeta.
  if not Exec(Setup, '/S /D=' + IprpDir(), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    if not WizardSilent() then
      MsgBox('No se pudo instalar el receptor de paneles de alarma (codigo ' + IntToStr(ResultCode) + ').' #13#10#13#10 +
             'Es un componente necesario: sin el, los paneles AX PRO y AX HYBRID PRO que reportan' #13#10 +
             'por ISUP/OTAP no podran conectarse. El resto del sistema quedo instalado; vuelva a' #13#10 +
             'ejecutar este instalador para reintentarlo.', mbError, MB_OK);
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Dejando el receptor de paneles accesible solo desde este equipo...';
  StopIprpService();
  if not PatchIprpNginx() then
    if not WizardSilent() then
      MsgBox('El receptor de paneles se instalo, pero no se pudo cambiar su puerto en nginx.conf.' #13#10 +
             'Quedara en el puerto 80 y accesible desde la red: cambielo en su configuracion.', mbInformation, MB_OK);
  PatchIprpConfigXml();
  if not HideIprpWebUi() then
    if not WizardSilent() then
      MsgBox('El receptor de paneles quedo instalado, pero no se pudo ocultar su interfaz web.' #13#10 +
             'No afecta al funcionamiento: esa interfaz solo es accesible desde este mismo equipo.', mbInformation, MB_OK);
  ConfigureIprpFirewall();

  RunHidden(SysTool('sc.exe'), 'config {#IprpService} start= auto');
  RunHidden(SysTool('sc.exe'), 'start {#IprpService}');

  // Verificacion: la receptora tarda en levantar sus servicios internos.
  Healthy := False;
  if FileExists(SysTool('curl.exe')) then
  begin
    WizardForm.StatusLabel.Caption := 'Esperando al receptor de paneles de alarma...';
    for I := 1 to 45 do
    begin
      // Se consulta la API y no la raiz, que quedo deshabilitada a proposito.
      // Un 401 (pide credenciales) ya demuestra que la receptora responde.
      if RunHidden(SysTool('curl.exe'),
           '--silent --output NUL --max-time 5 http://127.0.0.1:{#IprpWebPort}/ISAPI/System/deviceInfo?format=json') = 0 then
      begin
        Healthy := True;
        break;
      end;
      Sleep(2000);
    end;
    if not Healthy and not WizardSilent() then
      MsgBox('El receptor de paneles se instalo pero todavia no responde en 127.0.0.1:{#IprpWebPort}.' #13#10 +
             'Revise el servicio {#IprpService} en unos minutos.', mbInformation, MB_OK);
  end;
end;
#endif

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    TightenDataAcl();
    ConfigurePgPort();   // antes de arrancar: el clúster nace en el puerto elegido
#ifdef HasIprp
    // La receptora va ANTES de arrancar el servidor: este la activa sola en su
    // arranque y necesita encontrarla escuchando. Si se instalara despues, la
    // activacion quedaria pendiente hasta el siguiente reinicio del servicio.
    InstallIprp();
#endif
    ConfigureService();
    ConfigureFirewall();
    StartServiceAndVerify();
    InstallClient();
  end;
end;

// ---------------------------------------------------------------------------
// Desinstalación
// ---------------------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  I, ResultCode: Integer;
  ClientUninstaller: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillWatchdog();
    if ServiceExists() then
    begin
      RunHidden(SysTool('sc.exe'), 'stop {#ServiceName}');
      for I := 1 to 180 do
      begin
        if not ServiceRunning() then
          break;
        Sleep(500);
      end;
      Sleep(1000);
      RunHidden(SysTool('sc.exe'), 'delete {#ServiceName}');
    end;
    // Un postgres o mediamtx huérfano bloquearía sus propios binarios durante el borrado.
    StopOrphanPostgres();
    StopOrphanMediaMtx();
    DeleteFirewallRules();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // El cliente es un programa aparte: se ofrece quitarlo también.
    ClientUninstaller := ExpandConstant('{autopf}\CLR TrueCentral VMS\Client\unins000.exe');
    if FileExists(ClientUninstaller) and not UninstallSilent() then
      if MsgBox('¿Desea desinstalar también el cliente de escritorio de este equipo?',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        Exec(ClientUninstaller, '/VERYSILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if DirExists(DataDir()) and not UninstallSilent() then
      if MsgBox('¿Desea eliminar también los DATOS del sistema (base de datos, fotos, configuración y registros)?'#13#10#13#10 +
                DataDir() + #13#10#13#10 +
                'Si va a reinstalar o actualizar CLR TrueCentral VMS, elija "No".',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir(), True, True, True);
        DeleteFile(ExpandConstant('{app}\appsettings.Local.json'));
        DeleteFile(ExpandConstant('{app}\mediamtx.runtime.yml'));
        RemoveDir(ExpandConstant('{app}'));
      end;
  end;
end;
