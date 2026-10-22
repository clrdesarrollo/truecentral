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
;     ofrece abrir https://localhost.
;   - Puertos web: HTTPS 443 (todo), HTTP 80 (solo equipos sin TLS) y 5099
;     interno (loopback). Si 80 o 443 están ocupados, la instalación se detiene.
;
; Compilación:  installer\build-installers.ps1   (o ISCC /DAppVersion=x.y.z suite.iss)
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; appsettings.Production.json que se instala. build-installers.ps1 pasa una
; copia con la API key del servidor de licencias (que no va en git); sin ella
; se empaqueta la plantilla del repositorio (solo activación sin conexión).
#ifndef ProductionConfig
  #define ProductionConfig "config\appsettings.Production.json"
#endif

#define AppName        "CLR TrueCentral VMS"
#define Publisher      "CLRobotics"
#define ServiceName    "CLRTrueCentralVMS"
#define ServerExe      "TrueCentralVms.Server.exe"
#define DataDirName    "CLRTrueCentralVMS"
#define HttpsPort      "443"    ; panel, API, clientes, pantallas y agentes (Web:HttpsPort)
#define HttpPort       "80"     ; solo equipos sin TLS: paneles de alarma y terminales ISUP (Web:HttpPort)
#define InternalPort   "5099"   ; HTTP solo en loopback: sonda de salud y MediaMTX (Web:InternalPort)
; Debe coincidir con LicenseStateStore.RegistryPath del servidor.
#define LicenseRegistryKey "SOFTWARE\CLRobotics\TrueCentralVMS\Runtime"
#define ArcPort        "5091"
#define CercoPort      "5092"   ; receptor WebSocket de los paneles de cerco
#define CercoFwPort    "5093"   ; descarga de firmware (OTA) de los paneles de cerco
#define RtspPort       "8654"
#define WebRtcIcePort  "8660"   ; medio WebRTC de la vista en vivo del panel web (UDP y TCP)
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
; Hik IP Receiver Pro: hasta la 0.5.x los paneles AX PRO se conectaban a traves
; de esta receptora de Hikvision, que la suite instalaba. Desde la 0.6 el
; servidor trae su propio servicio ISUP (TrueCentralVms.IsupHost.exe) y la
; receptora ya no se instala: al actualizar se retira la que instalo este
; sistema (ver RetireIprp), porque ademas ocupa los mismos puertos.
#define IprpService    "DeviceGatewayService"
#define IprpDir        "{autopf}\Hik IP Receiver Pro"
; Puertos del servicio ISUP hacia los equipos (registro, alarmas, video, voz e
; imagenes): Isup:RegistrationPorts y siguientes en appsettings.json.
#define IsupTcpPorts   "7660-7667"
#define IsupUdpPorts   "7660,7663"

; Complemento de enrolamiento (lector de huellas USB): la suite lo publica en
; la carpeta webcontrol\ del servidor, que es de donde el panel web lo ofrece
; en descarga a los puestos que lo necesiten.
; build-installers.ps1 puede pasar /DAgentSetupName y /DClientSetupName con un instalador
; de una versión ANTERIOR que ya esté en dist\ (compilar solo la suite cuando el cliente y
; el complemento no cambiaron). El servidor los publica por patrón, sin mirar la versión.
#ifndef AgentSetupName
  #define AgentSetupName "CLRTrueCentralVMS-Complemento-Setup-" + AppVersion + ".exe"
#endif
#define AgentSetup     "..\dist\" + AgentSetupName
#if FileExists(AgentSetup)
  #define HasAgent
#else
  #pragma warning "No existe " + AgentSetup + ": la suite se compila SIN el complemento de enrolamiento (compile primero complemento.iss)."
#endif

; App de las pantallas de alarma de las casetas (Raspberry Pi, linux-arm64): la
; suite la deja en screen-packages\, de donde cada pantalla se actualiza sola
; (installer\screen\truecentral-screen-update.sh). build-installers.ps1 la genera.
#define ScreenPackage "..\dist\truecentral-screen-" + AppVersion + "-linux-arm64.tar.gz"
#if FileExists(ScreenPackage)
  #define HasScreenPackage
#else
  #pragma warning "No existe " + ScreenPackage + ": la suite se compila SIN la app de las pantallas de alarma."
#endif

#ifndef ClientSetupName
  #define ClientSetupName "CLRTrueCentralVMS-Client-Setup-" + AppVersion + ".exe"
#endif
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
; Identificacion del .exe del instalador. Sin esto, Windows lo muestra como
; "Setup/Uninstall" en el Administrador de tareas y en las propiedades del
; archivo, que no dice nada de que producto es.
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Instalador de {#AppName} (servidor)
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoCopyright=(c) {#Publisher}
VersionInfoOriginalFileName=CLRTrueCentralVMS-Suite-Setup.exe
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
; Complemento de enrolamiento de versiones anteriores (se publica el nuevo).
Type: files; Name: "{app}\webcontrol\CLRTrueCentralVMS-Complemento-Setup-*.exe"
; App de las pantallas de alarma de versiones anteriores (se publica la nueva).
Type: files; Name: "{app}\screen-packages\truecentral-screen-*-linux-arm64.tar.gz*"
; Demo de HCNetSDK que las versiones anteriores copiaban con las DLL de Hikvision,
; y los ajustes que deja al abrirla.
Type: files; Name: "{app}\ClientDemoEn.exe"
Type: filesandordirs; Name: "{app}\ClientDemoDll"
Type: files; Name: "{app}\DemoLocalCfg.json"
Type: files; Name: "{app}\DeviceCfg.json"

[Files]
; Servidor publicado (self-contained). Se excluyen los restos de desarrollo
; que el SDK Web arrastra como contenido (ajustes locales, configuración
; generada de MediaMTX, logs).
Source: "{#ServerPublish}\*"; DestDir: "{app}"; Excludes: "*.pdb,appsettings.Development.json,appsettings.Local.json,mediamtx.runtime.yml*,*.log,pgdata\*,anpr\*,workflows\*,backups\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProductionConfig}"; DestDir: "{app}"; DestName: "appsettings.Production.json"; Flags: ignoreversion
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

; Complemento de enrolamiento: NO se instala en el servidor (el lector se
; conecta al puesto del operador, no acá). Queda publicado para que el panel
; web lo ofrezca en descarga cuando alguien vaya a enrolar una huella y no lo
; tenga instalado — es la carpeta que mira /api/webcontrol/info.
#ifdef HasAgent
Source: "{#AgentSetup}"; DestDir: "{app}\webcontrol"; Flags: ignoreversion
#endif

; App de las pantallas de alarma: el servidor la ofrece a las Raspberry Pi de las
; casetas (GET /api/screen/client/version), que la instalan solas cada noche.
#ifdef HasScreenPackage
Source: "{#ScreenPackage}"; DestDir: "{app}\screen-packages"; Flags: ignoreversion
; Manifiesto firmado (build-installers.ps1): sin él las pantallas no se actualizan.
Source: "{#ScreenPackage}.manifest"; DestDir: "{app}\screen-packages"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#ScreenPackage}.manifest.sig"; DestDir: "{app}\screen-packages"; Flags: ignoreversion skipifsourcedoesntexist
#endif

[Icons]
Name: "{autoprograms}\CLR TrueCentral VMS\Panel web de CLR TrueCentral VMS"; Filename: "https://localhost"
Name: "{autoprograms}\CLR TrueCentral VMS\Watchdog de CLR TrueCentral VMS"; Filename: "{app}\watchdog\{#WatchdogExe}"
Name: "{autoprograms}\CLR TrueCentral VMS\Datos de CLR TrueCentral VMS"; Filename: "{commonappdata}\{#DataDirName}"
Name: "{autodesktop}\Watchdog de CLR TrueCentral VMS"; Filename: "{app}\watchdog\{#WatchdogExe}"; Tasks: desktopicon
#ifdef HasClient
Name: "{autoprograms}\CLR TrueCentral VMS\Instalador del cliente (para otros puestos)"; Filename: "{app}\client-setup"
#endif

[Run]
; Abre el panel: el primer administrador se crea desde el asistente, que
; solo acepta conexiones desde esta misma máquina.
Filename: "https://localhost"; Description: "Abrir el panel web (crear el primer administrador)"; Flags: postinstall shellexec skipifsilent nowait
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

// Para ESPERAR la detención no sirve "not ServiceRunning()": durante el apagado
// el estado es STOP_PENDING, que no contiene RUNNING, y la espera terminaba al
// instante con el servidor todavía vivo (la copia fallaba con "DeleteFile
// falló; código 5" sobre clrjit.dll, y además se le mataban postgres y
// mediamtx por debajo como si fueran huérfanos).
function ServiceStopped(): Boolean;
begin
  Result := RunHidden(SysTool('cmd.exe'), '/c sc query {#ServiceName} | find "STOPPED"') = 0;
end;

// Que el SCM diga STOPPED no garantiza que el proceso ya salió (puede seguir
// descargándose, o haber quedado colgado en el apagado), y mientras viva tiene
// mapeados sus .dll. También cubre un servidor lanzado a mano desde {app}.
// Solo se toca el de ESTA instalación; se fuerza si no sale en 30 s.
procedure WaitServerProcessExit();
begin
  RunHidden(PowerShellExe(),
    '-NoProfile -NonInteractive -Command "$p = @(Get-Process TrueCentralVms.Server -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' }); ' +
    'if ($p.Count) { $p | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue; ' +
    '$p | Where-Object { -not $_.HasExited } | Stop-Process -Force; Start-Sleep -Seconds 1 }"');
end;

procedure StopServiceAndWait(const Caption: string);
var
  I: Integer;
begin
  if ServiceExists() then
  begin
    if Caption <> '' then
    begin
      WizardForm.StatusLabel.Caption := Caption;
      WizardForm.PreparingLabel.Caption := Caption;
    end;
    RunHidden(SysTool('sc.exe'), 'stop {#ServiceName}');
    // Apagado ordenado: el servidor se da hasta 60 s (HostOptions.ShutdownTimeout,
    // incluye pg_ctl stop); aquí se espera hasta 2 min antes de forzar.
    for I := 1 to 240 do
    begin
      if ServiceStopped() then
        break;
      Sleep(500);
    end;
  end;
  WaitServerProcessExit();
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
    '-NoProfile -NonInteractive -Command "Get-Process mediamtx,ffmpeg,TrueCentralVms.IsupHost -ErrorAction SilentlyContinue | ' +
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

// ¿Algún programa ESCUCHA en ese puerto? A diferencia de PortInUse, no da
// positivo por conexiones salientes (cualquier navegador tiene alguna hacia un
// :443 remoto). Get-NetTCPConnection no depende del idioma de Windows.
function ListeningOn(Port: Integer): Boolean;
begin
  Result := RunHidden(PowerShellExe(),
    '-NoProfile -NonInteractive -Command "if (Get-NetTCPConnection -State Listen -LocalPort ' + IntToStr(Port) +
    ' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"') = 0;
end;

// Los puertos web del servidor (443 y 80) deben estar libres: con IIS, otro
// servidor web o un programa que los reservó, el servicio no podría arrancar.
// Se revisa con nuestro servicio ya detenido. Si este equipo los cambió en
// appsettings.Local.json ("Web"), se respeta y no se revisa.
function WebPortsConflict(): String;
var
  Local: AnsiString;
  Busy: String;
begin
  Result := '';
  if LoadStringFromFile(ExpandConstant('{app}\appsettings.Local.json'), Local) and (Pos('"HttpsPort"', Local) > 0) then
    exit;
  Busy := '';
  if ListeningOn({#HttpsPort}) then Busy := '{#HttpsPort} (HTTPS)';
  if ListeningOn({#HttpPort}) then
  begin
    if Busy <> '' then Busy := Busy + ' y ';
    Busy := Busy + '{#HttpPort} (HTTP)';
  end;
  if Busy <> '' then
    Result := 'El puerto ' + Busy + ' ya lo usa otro programa de este equipo (por ejemplo IIS u otro servidor web).' + #13#10#13#10 +
              'CLR TrueCentral VMS atiende el panel web y a los clientes por HTTPS en el 443, y a los equipos sin cifrado ' +
              '(paneles de alarma, terminales ISUP) en el 80. Libere esos puertos (detenga o reconfigure el otro programa) ' +
              'y vuelva a ejecutar el instalador.' + #13#10#13#10 +
              'Si este equipo debe usar otros puertos, defínalos en "Web" de appsettings.Local.json antes de instalar.';
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
  // El texto del paso lo pone CurStepChanged (ShowStep).

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
  Result := WebPortsConflict();
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
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS MediaMTX (WebRTC UDP)"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS MediaMTX (WebRTC TCP)"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS ISUP"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS ISUP (UDP)"');
end;

procedure ConfigureFirewall();
begin
  DeleteFirewallRules();
  // Panel web + API + SignalR por HTTPS (443), HTTP de los equipos sin TLS (80),
  // receptor de alarmas SIA DC-09 (5091) y
  // receptor WebSocket de los paneles de cerco (5092; sin esta regla los paneles
  // no llegan al servidor desde otro equipo) y descarga de firmware OTA (5093).
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS Server" dir=in action=allow protocol=TCP localport={#HttpsPort},{#HttpPort},{#ArcPort},{#CercoPort},{#CercoFwPort} profile=any');
  // Respuestas del descubrimiento de equipos: SADP (UDP 37020), WS-Discovery
  // (UDP 3702), Dahua (UDP 37810) y ZKTeco (UDP 4370, control de acceso), y el
  // autodiscovery de las pantallas de alarma de las casetas (UDP 5094).
  // La regla va POR PROGRAMA, no por puerto: alcanza para cualquier familia
  // que se agregue despues sin tocar el firewall.
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS Server (descubrimiento)" dir=in action=allow protocol=UDP program="'
    + ExpandConstant('{app}') + '\{#ServerExe}" profile=any');
  // RTSP hacia los espectadores (clientes de escritorio y decodificadores).
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS MediaMTX" dir=in action=allow protocol=TCP localport={#RtspPort} program="'
    + ExpandConstant('{app}') + '\tools\mediamtx\mediamtx.exe" profile=any');
  // Vista en vivo del panel web: el navegador recibe el video por WebRTC
  // directo desde MediaMTX por este puerto (UDP; TCP para redes que bloquean
  // UDP). La señalización va por el puerto web del servidor.
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS MediaMTX (WebRTC UDP)" dir=in action=allow protocol=UDP localport={#WebRtcIcePort} program="'
    + ExpandConstant('{app}') + '\tools\mediamtx\mediamtx.exe" profile=any');
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS MediaMTX (WebRTC TCP)" dir=in action=allow protocol=TCP localport={#WebRtcIcePort} program="'
    + ExpandConstant('{app}') + '\tools\mediamtx\mediamtx.exe" profile=any');
  // Servicio ISUP: los equipos detras de NAT o 4G se conectan ellos al
  // servidor (registro 7660 y 7661, alarmas, video, reproduccion, voz e
  // imagenes hasta el 7667; UDP para el registro y las alarmas de ISUP 2.0/4.0).
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS ISUP" dir=in action=allow protocol=TCP localport={#IsupTcpPorts} program="'
    + ExpandConstant('{app}') + '\TrueCentralVms.IsupHost.exe" profile=any');
  RunHidden(SysTool('netsh.exe'),
    'advfirewall firewall add rule name="CLR TrueCentral VMS ISUP (UDP)" dir=in action=allow protocol=UDP localport={#IsupUdpPorts} program="'
    + ExpandConstant('{app}') + '\TrueCentralVms.IsupHost.exe" profile=any');
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

// La configuración del servidor también queda solo para SYSTEM y
// Administradores: appsettings.Production.json trae la API key del servidor de
// licencias y appsettings.Local.json (y su .bak) puede traerla (servidores a los
// que se les puso a mano). El resto de {app} sigue legible para todos; quien lee estos
// archivos es el servicio (LocalSystem) y el Watchdog, que corre elevado.
// Va en cada instalación: el archivo que una actualización reemplaza nace con la
// ACL heredada de la carpeta. Sin (OI)(CI): son archivos (ver TightenDataAcl).
procedure TightenConfigFileAcl(const FileName: string);
begin
  if FileExists(FileName) then
    RunHidden(SysTool('icacls.exe'),
      '"' + FileName + '" /inheritance:r /grant:r *S-1-5-18:F /grant:r *S-1-5-32-544:F /Q');
end;

procedure TightenConfigAcl();
begin
  TightenConfigFileAcl(ExpandConstant('{app}\appsettings.Production.json'));
  TightenConfigFileAcl(ExpandConstant('{app}\appsettings.Local.json'));
  TightenConfigFileAcl(ExpandConstant('{app}\appsettings.Local.json.bak'));
end;

// ---------------------------------------------------------------------------
// Avance visible de la instalación
//
// Después de copiar los archivos quedan pasos que pueden tardar minutos (sobre
// todo el arranque del servidor en una actualización: respaldo completo de la
// base y migraciones). Cada paso dice qué está haciendo, la barra avanza cuando
// se puede medir y se anima cuando no, y se muestra el tiempo transcurrido: la
// pantalla nunca queda quieta como si se hubiera colgado.
// ---------------------------------------------------------------------------
const
  PostSteps = 6;   // pasos numerados después de copiar los archivos

var
  Summary: string;   // lo que se hizo, para la página final

function GetTickCount(): DWord; external 'GetTickCount@kernel32.dll stdcall';

procedure AddSummary(const Line: string);
begin
  Summary := Summary + #13#10 + '• ' + Line;
end;

// Texto del paso ("Paso 3 de 6 · ...") y detalle. Percent: 0..100 mueve la
// barra; -1 la anima (no se puede medir).
procedure ShowStep(Step: Integer; const Caption, Detail: string; Percent: Integer);
begin
  if Step > 0 then
    WizardForm.StatusLabel.Caption := 'Paso ' + IntToStr(Step) + ' de ' + IntToStr(PostSteps) + ' · ' + Caption
  else
    WizardForm.StatusLabel.Caption := Caption;
  WizardForm.FilenameLabel.Caption := Detail;
  if Percent < 0 then
    WizardForm.ProgressGauge.Style := npbstMarquee
  else
  begin
    WizardForm.ProgressGauge.Style := npbstNormal;
    WizardForm.ProgressGauge.Position := WizardForm.ProgressGauge.Min +
      (WizardForm.ProgressGauge.Max - WizardForm.ProgressGauge.Min) * Percent div 100;
  end;
end;

function Elapsed(StartTick: DWord): string;
var
  Seconds: Integer;
begin
  Seconds := (GetTickCount() - StartTick) div 1000;
  if Seconds < 60 then
    Result := IntToStr(Seconds) + ' s'
  else
    Result := IntToStr(Seconds div 60) + ' min ' + IntToStr(Seconds mod 60) + ' s';
end;

// Espera sin congelar la ventana: Exec atiende los mensajes mientras espera
// (Sleep no, y la barra animada se detendría). ping -n 2 tarda ~1 s.
procedure PauseOneSecond();
begin
  RunHidden(SysTool('ping.exe'), '-n 2 127.0.0.1');
end;

function StartupStatusFile(): string;
begin
  Result := DataDir() + '\startup-status.ini';
end;

function StartupValue(const Key: string): string;
begin
  Result := GetIniString('status', Key, '', StartupStatusFile());
end;

// Arranca el servicio y espera a que la API responda: en ese momento el
// esquema ya fue creado/migrado por el servidor. Mientras tanto el servidor
// informa en qué etapa va en startup-status.ini (base, respaldo, migraciones,
// servicios) y eso es lo que se muestra.
procedure StartServiceAndVerify();
var
  Healthy, Crashed, Stalled, SawBackup: Boolean;
  StartTick, LastChangeTick: DWord;
  Phase, Title, Detail, Updated, LastUpdated, Migrations: string;
  Percent: Integer;
begin
  Crashed := False;
  Healthy := False;
  Stalled := False;
  SawBackup := False;
  Migrations := '';
  // Un estado de un arranque anterior confundiría la lectura.
  DeleteFile(StartupStatusFile());
  ShowStep(5, 'Iniciando el servidor', 'Arrancando el servicio {#ServiceName}', -1);
  RunHidden(SysTool('sc.exe'), 'start {#ServiceName}');
  if not FileExists(SysTool('curl.exe')) then
    exit;

  StartTick := GetTickCount();
  LastChangeTick := StartTick;
  LastUpdated := '';
  // Por el puerto interno (HTTP solo en loopback, sin certificado de por
  // medio) y por 127.0.0.1 y no "localhost": resolver ::1 primero podría
  // consumir el --max-time y el chequeo fallaría con el servicio sano.
  while True do
  begin
    if RunHidden(SysTool('curl.exe'),
         '--silent --fail --max-time 3 http://127.0.0.1:{#InternalPort}/api/health') = 0 then
    begin
      Healthy := True;
      break;
    end;
    if not ServiceRunning() then
    begin
      Crashed := True;
      break;
    end;

    Phase := StartupValue('phase');
    Title := StartupValue('title');
    Detail := StartupValue('detail');
    Percent := StrToIntDef(StartupValue('percent'), -1);
    Updated := StartupValue('updated');
    if Phase = 'backup' then
      SawBackup := True;
    if Phase = 'migrate' then
      Migrations := Detail;
    if Phase = 'error' then
    begin
      Crashed := True;
      break;
    end;
    if Updated <> LastUpdated then
    begin
      LastUpdated := Updated;
      LastChangeTick := GetTickCount();
    end;

    if Title = '' then
      Title := 'Esperando a que el servidor inicie';
    if Detail <> '' then
      Detail := Detail + '  ·  ';
    Detail := Detail + Elapsed(StartTick);
    if (GetTickCount() - LastChangeTick) > 60000 then
      Detail := Detail + '  ·  Sigue trabajando: con bases grandes este paso puede tardar varios minutos';
    ShowStep(5, Title, Detail, Percent);

    // Sin ninguna novedad del servidor en 10 minutos: algo se trabó.
    if (GetTickCount() - LastChangeTick) > 600000 then
    begin
      Stalled := True;
      break;
    end;
    PauseOneSecond();
  end;

  if Healthy then
  begin
    AddSummary('El servidor quedó funcionando (inició en ' + Elapsed(StartTick) + ').');
    if SawBackup then
      AddSummary('Antes de actualizar se respaldó la base completa en ' + DataDir() + '\backups.');
    if Migrations <> '' then
      AddSummary('Se actualizó la estructura de la base de datos.');
  end
  else if Crashed and not WizardSilent() then
    MsgBox('El servicio se instaló, pero se detiene al arrancar.' #13#10#13#10 +
           StartupValue('title') + ': ' + StartupValue('detail') + #13#10#13#10 +
           'Revise el Visor de eventos de Windows (Registro de aplicaciones, origen TrueCentralVms.Server).' #13#10 +
           'Causas habituales: el puerto {#HttpsPort} o el {#HttpPort} está ocupado por otro programa, o la base de datos' #13#10 +
           'no pudo iniciarse (puerto {#PgPort} o permisos sobre ' + DataDir() + ').',
           mbError, MB_OK)
  else if not WizardSilent() then
    MsgBox('El servicio se instaló y sigue iniciando, pero lleva ' + Elapsed(LastChangeTick) + ' sin avanzar' +
           ' (última etapa: ' + StartupValue('title') + ').' #13#10#13#10 +
           'Puede seguir trabajando solo. Revise el Visor de eventos de Windows o abra https://localhost en unos minutos.',
           mbInformation, MB_OK);
  if Stalled then
    AddSummary('El servidor seguía iniciando al cerrar el instalador.');
end;

// Cliente de escritorio en el mismo equipo: se instala con SU propio
// instalador (queda como programa aparte, actualizable por separado).
procedure InstallClient();
var
  Setup, Log: string;
  ResultCode: Integer;
  StartTick: DWord;
begin
#ifdef HasClient
  if not WizardIsTaskSelected('installclient') then
    exit;
  Setup := ExpandConstant('{app}\client-setup\{#ClientSetupName}');
  if not FileExists(Setup) then
    exit;
  Log := DataDir() + '\logs\client-setup.log';
  StartTick := GetTickCount();
  ShowStep(6, 'Instalando el cliente de escritorio',
    'Se cierra el cliente si está abierto y se copian sus archivos (suele tardar menos de un minuto)', -1);
  if not Exec(Setup, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG="' + Log + '"', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    if not WizardSilent() then
      MsgBox('El cliente de escritorio no se pudo instalar (código ' + IntToStr(ResultCode) + ').' #13#10 +
             'Puede instalarlo a mano desde ' + Setup + #13#10 + 'Detalle en ' + Log,
             mbError, MB_OK);
    AddSummary('El cliente de escritorio NO se pudo instalar (detalle en ' + Log + ').');
  end
  else
    AddSummary('Cliente de escritorio instalado (' + Elapsed(StartTick) + ').');
#endif
end;

// ---------------------------------------------------------------------------
// Receptora anterior de paneles (Hik IP Receiver Pro)
//
// Hasta la 0.5.x los paneles AX PRO se conectaban a traves del Hik IP Receiver
// Pro, que esta suite instalaba como componente obligatorio. Ahora el servidor
// tiene su propio servicio ISUP y la receptora sobra; ademas escucha en los
// mismos puertos (7660-7667), asi que el servicio ISUP no podria abrirlos con
// ella en marcha. Al actualizar se quita la que instalo este sistema (la
// reconoce por tcvms-iprp.secret, que dejaba el servidor al activarla); una
// receptora ajena se respeta y solo se avisa del choque de puertos. Los
// paneles se reconectan solos: el servicio ISUP escucha tambien el 7661, donde
// se registraban, y la migracion IsupConnections les conserva ID y clave.
// ---------------------------------------------------------------------------
function IprpDir(): string;
begin
  Result := ExpandConstant('{#IprpDir}');
end;

// «No esta RUNNING» no basta: durante el apagado el servicio pasa por
// STOP_PENDING. Hay que esperar a STOPPED de verdad antes de desinstalarlo.
function IprpServiceStopped(): Boolean;
begin
  Result := RunHidden(SysTool('cmd.exe'), '/c sc query {#IprpService} | find "STOPPED"') = 0;
end;

procedure StopIprpService();
var
  I: Integer;
begin
  RunHidden(SysTool('sc.exe'), 'stop {#IprpService}');
  // El receptor apaga su nginx y su propio PostgreSQL: puede tardar. Hasta 2 min.
  for I := 1 to 240 do
  begin
    if IprpServiceStopped() then
      break;
    Sleep(500);
  end;
  Sleep(1000);
end;

// Reglas de firewall que la suite le ponia a la receptora (versiones anteriores).
procedure DeleteIprpFirewallRules();
begin
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS IP Receiver Pro (solo local)"');
  RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS paneles ISUP"');
end;

// ¿La receptora es la que instalo y administraba este sistema? La marca es su
// credencial, que el servidor de las versiones anteriores generaba al activarla.
function IprpManagedByUs(): Boolean;
begin
  Result := FileExists(DataDir() + '\pgdata\tcvms-iprp.secret');
end;

function IprpServiceExists(): Boolean;
begin
  Result := RunHidden(SysTool('sc.exe'), 'query {#IprpService}') = 0;
end;

// Quita la receptora que instalo este sistema (al actualizar a la 0.6 o al
// desinstalar).
//
// Su desinstalador es NSIS: /S es silencioso y devuelve el control enseguida,
// asi que hay que esperar a que el servicio desaparezca de verdad antes de
// borrar la carpeta (si no, quedan binarios tomados y el DelTree no limpia).
procedure UninstallIprp();
var
  Uninstaller: string;
  ResultCode, I: Integer;
begin
  DeleteIprpFirewallRules();
  if not IprpServiceExists() and not DirExists(IprpDir()) then
    exit;

  StopIprpService();
  Uninstaller := IprpDir() + '\uninst.exe';
  if FileExists(Uninstaller) then
  begin
    Exec(Uninstaller, '/S', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for I := 1 to 60 do
    begin
      if not IprpServiceExists() then
        break;
      Sleep(1000);
    end;
    Sleep(2000);
  end
  else
    // Sin desinstalador (instalacion rota o a medias) el servicio quedaria
    // registrado apuntando a una carpeta que estamos por borrar.
    RunHidden(SysTool('sc.exe'), 'delete {#IprpService}');

  DelTree(IprpDir(), True, True, True);
end;

procedure RetireIprp();
begin
  if not IprpServiceExists() and not DirExists(IprpDir()) then
  begin
    DeleteIprpFirewallRules();
    exit;
  end;
  if IprpManagedByUs() then
  begin
    ShowStep(2, 'Quitando la receptora de paneles anterior (Hik IP Receiver Pro)', 'Puede tardar hasta dos minutos', -1);
    UninstallIprp();
    DeleteFile(DataDir() + '\pgdata\tcvms-iprp.secret');
    exit;
  end;
  // Una receptora que no instalo este sistema no se toca.
  DeleteIprpFirewallRules();
  if not WizardSilent() then
    MsgBox('En este equipo hay un Hik IP Receiver Pro que no instalo CLR TrueCentral VMS.' #13#10#13#10 +
           'El servidor ahora tiene su propio servicio ISUP, que usa los mismos puertos (7660 a 7667): ' +
           'mientras ese receptor este en marcha, los equipos ISUP no podran conectarse al VMS.' #13#10#13#10 +
           'Si ya no lo usa, desinstalelo desde Aplicaciones instaladas (o detenga su servicio {#IprpService}).',
           mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartTick: DWord;
begin
  if CurStep = ssPostInstall then
  begin
    StartTick := GetTickCount();
    AddSummary('Archivos de la versión {#AppVersion} copiados.');
    ShowStep(1, 'Protegiendo la carpeta de datos', 'Solo el sistema y los administradores pueden leer la base y sus llaves', -1);
    TightenDataAcl();
    ShowStep(1, 'Comprobando el puerto de la base de datos', '', -1);
    ConfigurePgPort();   // antes de arrancar: el clúster nace en el puerto elegido
    // Después de ConfigurePgPort, que puede reescribir appsettings.Local.json y
    // dejar su .bak.
    ShowStep(1, 'Protegiendo la configuración del servidor', 'La API key de licencias queda solo para el sistema y los administradores', -1);
    TightenConfigAcl();
    // Antes de arrancar el servidor: su servicio ISUP necesita los puertos que
    // ocupaba la receptora anterior.
    ShowStep(2, 'Revisando componentes anteriores', '', -1);
    RetireIprp();
    ShowStep(3, 'Registrando el servicio de Windows', '{#ServiceName}, inicio automático y reinicio ante fallas', -1);
    ConfigureService();
    ShowStep(4, 'Configurando el firewall de Windows',
      'HTTPS {#HttpsPort}, HTTP {#HttpPort} (equipos sin cifrado), video, alarmas, cerco e ISUP', -1);
    ConfigureFirewall();
    StartServiceAndVerify();
    InstallClient();
    AddSummary('Tiempo total después de copiar: ' + Elapsed(StartTick) + '.');
    ShowStep(0, 'Instalación terminada', '', 100);
  end;
end;

// Página final: qué se hizo y cuánto tardó, además del texto de siempre.
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (Summary <> '') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10 + Summary;
end;

// ---------------------------------------------------------------------------
// Desinstalación
// ---------------------------------------------------------------------------
// CLR TrueCentral VMS es una solución cerrada: nada de lo que instala tiene
// sentido sin el resto. Desinstalar significa BORRADO TOTAL — el servicio, la
// base de datos, el cliente, el complemento, las
// reglas de firewall y las preferencias de los operadores. No se pregunta por
// partes: se avisa UNA vez, al principio (InitializeUninstall), y a partir de
// ahí no queda nada a medias.
//
// Actualizar NO pasa por acá: el instalador de la versión nueva se ejecuta
// encima del anterior (mismo AppId) y conserva los datos. El único camino a
// este código es que alguien decida quitar el producto.
// ---------------------------------------------------------------------------

// Barrido final, en dos partes, y en segundo plano.
//
// 1) Restos por usuario. Viven en el perfil de cada operador, así que ni el
//    desinstalador del cliente ni el del complemento —que corren para UN
//    usuario— alcanzan a limpiarlos todos. Se recorren los perfiles reales de
//    la máquina (Win32_UserProfile, sin los del sistema) y se borran solo
//    estas carpetas, por nombre exacto:
//      Roaming\CLRTrueCentralVMS    client.json y web-control.json
//      Local\CLRTrueCentral         registros de la proyección de pantalla
//      Local\CLRobotics\TrueCentral migrador.json (puede guardar credenciales
//                                   de HikCentral si se marcó recordarlas)
//
// 2) La carpeta del producto en Archivos de programa. No se puede borrar desde
//    acá: adentro está el unins000.exe que en este momento se está ejecutando,
//    y el del cliente, que corre en paralelo. Cada uno se borra a sí mismo al
//    terminar, pero la carpeta PADRE que los contiene no la quita nadie. Por
//    eso el script espera —hasta dos minutos— y va sacando únicamente
//    carpetas VACÍAS: nunca borra un archivo, así que no puede llevarse por
//    delante el unins000.dat que el desinstalador todavía está leyendo.
//
// Se lanza sin esperarlo (ewNoWait) y por eso NO puede vivir en {tmp}: esa
// carpeta desaparece cuando el desinstalador termina, que es justo mientras el
// script sigue corriendo. Va al temp del usuario y se borra a sí mismo.
procedure LaunchFinalCleanup();
var
  Script: TArrayOfString;
  Path: string;
  ResultCode: Integer;
begin
  Path := AddBackslash(GetTempDir()) + 'clr-truecentral-limpieza-final.ps1';
  SetArrayLength(Script, 27);
  Script[0]  := '# Barrido final de CLR TrueCentral VMS: restos por usuario y la carpeta';
  Script[1]  := '# del producto, cuando los desinstaladores terminen de soltarla.';
  Script[2]  := '$rutas = ''AppData\Roaming\CLRTrueCentralVMS'',''AppData\Local\CLRTrueCentral'',''AppData\Local\CLRobotics\TrueCentral''';
  Script[3]  := '$perfiles = Get-CimInstance Win32_UserProfile -ErrorAction SilentlyContinue |';
  Script[4]  := '  Where-Object { -not $_.Special -and $_.LocalPath } | ForEach-Object { $_.LocalPath }';
  Script[5]  := 'foreach ($perfil in $perfiles) {';
  Script[6]  := '  foreach ($rel in $rutas) {';
  Script[7]  := '    $ruta = Join-Path $perfil $rel';
  Script[8]  := '    if (Test-Path -LiteralPath $ruta) {';
  Script[9]  := '      Remove-Item -LiteralPath $ruta -Recurse -Force -ErrorAction SilentlyContinue';
  Script[10] := '    }' + #13#10 + '  }';
  // CLRobotics es la carpeta de la empresa, no del producto: solo se quita si
  // quedo vacia (otro producto nuestro podria estar usandola).
  Script[11] := '  $empresa = Join-Path $perfil ''AppData\Local\CLRobotics''';
  Script[12] := '  if ((Test-Path -LiteralPath $empresa) -and';
  Script[13] := '      -not (Get-ChildItem -LiteralPath $empresa -Force -ErrorAction SilentlyContinue)) {';
  Script[14] := '    Remove-Item -LiteralPath $empresa -Force -ErrorAction SilentlyContinue';
  Script[15] := '  }' + #13#10 + '}';
  Script[16] := '$raiz = ''' + ExpandConstant('{autopf}\CLR TrueCentral VMS') + '''';
  Script[17] := 'for ($i = 0; $i -lt 60; $i++) {';
  Script[18] := '  Start-Sleep -Seconds 2';
  Script[19] := '  if (-not (Test-Path -LiteralPath $raiz)) { break }';
  Script[20] := '  # Solo carpetas VACIAS: los desinstaladores todavia pueden estar usando las suyas.';
  Script[21] := '  Get-ChildItem -LiteralPath $raiz -Directory -Force -ErrorAction SilentlyContinue |';
  Script[22] := '    Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue) } |';
  Script[23] := '    Remove-Item -Force -ErrorAction SilentlyContinue';
  Script[24] := '  if (-not (Get-ChildItem -LiteralPath $raiz -Force -ErrorAction SilentlyContinue)) {';
  Script[25] := '    Remove-Item -LiteralPath $raiz -Force -ErrorAction SilentlyContinue; break';
  Script[26] := '  }' + #13#10 + '}' + #13#10 +
                'Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue';
  if not SaveStringsToFile(Path, Script, False) then
    exit;
  Exec(PowerShellExe(), '-NoProfile -ExecutionPolicy Bypass -File "' + Path + '"',
       '', SW_HIDE, ewNoWait, ResultCode);
end;

// Lee una propiedad de texto de un JSON escrito con indentación (una propiedad
// por línea): «"clave": "valor",». No es un parser: alcanza para los archivos
// que escribe el servidor, que salen siempre con ese formato.
function JsonStringValue(const Path, Key: string): string;
var
  Lines: TArrayOfString;
  Value: string;
  I, P: Integer;
begin
  Result := '';
  if not FileExists(Path) then
    exit;
  if not LoadStringsFromFile(Path, Lines) then
    exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    P := Pos('"' + Key + '"', Lines[I]);
    if P = 0 then
      continue;
    Value := Copy(Lines[I], P + Length(Key) + 2, Length(Lines[I]));
    P := Pos(':', Value);
    if P = 0 then
      exit;
    Value := Trim(Copy(Value, P + 1, Length(Value)));
    // Sobran la coma de separación y las comillas del valor.
    if (Length(Value) > 0) and (Value[Length(Value)] = ',') then
      Value := Trim(Copy(Value, 1, Length(Value) - 1));
    if (Length(Value) >= 2) and (Value[1] = '"') and (Value[Length(Value)] = '"') then
      Result := Copy(Value, 2, Length(Value) - 2);
    exit;
  end;
end;

function LicenseDir(): string;
begin
  Result := DataDir() + '\license';
end;

// ¿Hay una licencia ACTIVADA en este equipo? Devuelve su código de activación,
// o cadena vacía si no hay ninguna.
//
// La marca fiable es activation.json, la pista en claro que el servidor deja
// para este aviso (su estado real va cifrado): existe solo mientras hay una
// licencia activada. Las versiones anteriores guardaban el código en
// state.json. Desactivar desde el panel borra la pista (y el .lic firmado
// puede quedar en disco si Windows lo tenía tomado en ese momento —
// LicenseService ignora ese IOException y reintenta en el próximo arranque);
// por eso el .lic solo cuenta cuando no hay ninguna de las dos.
function ActivatedLicenseCode(): string;
begin
  Result := '';
  if FileExists(LicenseDir() + '\activation.json') then
  begin
    Result := JsonStringValue(LicenseDir() + '\activation.json', 'activationCode');
    exit;
  end;
  if FileExists(LicenseDir() + '\state.dat') then
    exit;
  if FileExists(LicenseDir() + '\state.json') then
  begin
    Result := JsonStringValue(LicenseDir() + '\state.json', 'activationCode');
    exit;
  end;
  if FileExists(LicenseDir() + '\license.lic') then
    Result := '(no se pudo leer el código)';
end;

// Aviso de licencia activa. Va PRIMERO y aparte del aviso general porque es lo
// único que todavía tiene arreglo: en este punto el servicio sigue corriendo,
// así que el panel web responde y la licencia se puede liberar. En cuanto
// empiece la desinstalación esa puerta se cierra — el servidor de licencias
// seguiría contando la activación como usada y el equipo nuevo no podría
// tomarla. Devuelve False si el operador prefiere cancelar y liberarla.
function ConfirmActiveLicense(): Boolean;
var
  Code: string;
begin
  Result := True;
  Code := ActivatedLicenseCode();
  if Code = '' then
    exit;
  Result := MsgBox('ATENCIÓN: este equipo tiene una LICENCIA ACTIVA.' #13#10#13#10 +
                   '    Código de activación:  ' + Code + #13#10#13#10 +
                   'Al desinstalar, la licencia se borra de este equipo, pero el servidor de' #13#10 +
                   'licencias la va a seguir contando como usada: NO se va a poder activar' #13#10 +
                   'en otro equipo hasta liberarla, y desde acá ya no hay forma de hacerlo.' #13#10#13#10 +
                   'Para liberarla hay que desactivarla ANTES, con el sistema todavía' #13#10 +
                   'funcionando:' #13#10#13#10 +
                   '    1. Cancele esta desinstalación.' #13#10 +
                   '    2. Abra https://localhost y entre a Sistema > Licencia.' #13#10 +
                   '    3. Use "Desactivar en este equipo".' #13#10 +
                   '    4. Vuelva a desinstalar.' #13#10#13#10 +
                   '¿Desea desinstalar igual, SIN liberar la licencia?',
                   mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

// Aviso único: es la última oportunidad de echarse atrás. En desinstalación
// silenciosa no se pregunta nada (quien la lanza ya sabe lo que hace).
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if UninstallSilent() then
    exit;
  if not ConfirmActiveLicense() then
  begin
    Result := False;
    exit;
  end;
  Result := MsgBox('{#AppName} se va a eliminar POR COMPLETO de este equipo:' #13#10#13#10 +
                   '  - el servidor y su servicio de Windows;' #13#10 +
                   '  - la BASE DE DATOS con todo su contenido: personas, huellas, tarjetas,' #13#10 +
                   '    permisos, eventos, grabaciones, configuración y la licencia;' #13#10 +
                   '  - el receptor de paneles de alarma, con su historial y los paneles' #13#10 +
                   '    que tenga registrados;' #13#10 +
                   '  - el cliente de escritorio y el complemento de enrolamiento;' #13#10 +
                   '  - las reglas de firewall y las preferencias de los operadores;' #13#10 +
                   '  - los RESPALDOS guardados en este equipo (carpeta backups). Los que' #13#10 +
                   '    estén en el segundo destino configurado NO se borran.' #13#10#13#10 +
                   'NO se puede deshacer y NO queda copia de la base de datos en este equipo.' #13#10 +
                   'Si quiere conservar un respaldo, cópielo antes a otro lugar.' #13#10#13#10 +
                   'Si lo que quiere es ACTUALIZAR el sistema, cancele: ejecute el instalador' #13#10 +
                   'de la versión nueva encima de esta, que conserva todos los datos.' #13#10#13#10 +
                   '¿Desea continuar?',
                   mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

// Borra del registro todo valor de la clave de licencia salvo la marca de
// prueba («Profile»). Ver LicenseStateStore en el servidor.
procedure DeleteLicenseStateValues();
var
  Names: TArrayOfString;
  I: Integer;
begin
  if not RegGetValueNames(HKLM64, '{#LicenseRegistryKey}', Names) then
    exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if CompareText(Names[I], 'Profile') <> 0 then
      RegDeleteValue(HKLM64, '{#LicenseRegistryKey}', Names[I]);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Uninstaller: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillWatchdog();
    StopServiceAndWait('');
    if ServiceExists() then
      RunHidden(SysTool('sc.exe'), 'delete {#ServiceName}');
    // Un postgres o mediamtx huérfano bloquearía sus propios binarios durante el borrado.
    StopOrphanPostgres();
    StopOrphanMediaMtx();
    DeleteFirewallRules();
    // La receptora de versiones anteriores, si quedó (normalmente la retira la
    // actualización). Antes de borrar archivos: su desinstalador tarda.
    if IprpManagedByUs() then
      UninstallIprp();
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // El cliente y el complemento son programas aparte, con su propio
    // desinstalador: se lanzan en silencio para que también desaparezcan de
    // "Aplicaciones instaladas".
    Uninstaller := ExpandConstant('{autopf}\CLR TrueCentral VMS\Client\unins000.exe');
    if FileExists(Uninstaller) then
      Exec(Uninstaller, '/VERYSILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // El complemento se instala por usuario (PrivilegesRequired=lowest), así
    // que vive bajo el perfil, no en Archivos de programa.
    Uninstaller := ExpandConstant('{localappdata}\Programs\CLR TrueCentral VMS\Complemento\unins000.exe');
    if FileExists(Uninstaller) then
      Exec(Uninstaller, '/VERYSILENT /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Datos del sistema: base de datos, fotos, matrículas, workflows, licencia,
    // registros y los respaldos locales (borrado total, decisión 2026-10-20; el
    // segundo destino de los respaldos no se toca).
    DelTree(DataDir(), True, True, True);

    // Copias del estado de licencia en el registro (las escribe el servicio,
    // como LocalSystem, en la vista de 64 bits: un valor «Data-<huella>» por
    // carpeta de datos). La marca de prueba («Profile», en la misma clave) se
    // CONSERVA a propósito: reinstalar no regala otro período de prueba. No
    // guarda datos del cliente.
    DeleteLicenseStateValues();

    // Lo que queda en {app} después de que Inno borró los archivos del registro
    // de instalación: los ajustes del equipo (marcados uninsneveruninstall), la
    // configuración que MediaMTX genera en cada arranque y los certificados que
    // MediaMTX se crea solo — nada de eso está en ese registro, así que hay que
    // nombrarlo. OJO: no se puede hacer un DelTree de {app} entero, porque ahí
    // adentro está el propio unins000.dat que el desinstalador todavía usa para
    // terminar (él mismo se borra al final, y con él la carpeta si queda vacía).
    DeleteFile(ExpandConstant('{app}\appsettings.Local.json'));
    DeleteFile(ExpandConstant('{app}\mediamtx.runtime.yml'));
    DeleteFile(ExpandConstant('{app}\mediamtx.runtime.yml.tmp'));
    DelTree(ExpandConstant('{app}\tools'), True, True, True);
    DelTree(ExpandConstant('{app}\wwwroot'), True, True, True);
    DelTree(ExpandConstant('{app}\watchdog'), True, True, True);
    DelTree(ExpandConstant('{app}\client-setup'), True, True, True);
    DelTree(ExpandConstant('{app}\webcontrol'), True, True, True);

    LaunchFinalCleanup();
  end;
end;
