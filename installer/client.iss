; ============================================================================
; CLR TrueCentral VMS — Instalador del CLIENTE de operación (Inno Setup 6)
; ----------------------------------------------------------------------------
; Instala el cliente WPF en cada puesto de operación, junto con:
;   - FFmpeg de FlyleafLib (carpeta FFmpeg\ junto al exe: motor de video)
;   - ffmpeg/ffprobe de línea de comandos (tools\ffmpeg\bin: proyección de
;     pantalla al muro y controles de reproducción de archivos)
;   - MediaMTX (tools\mediamtx: publica la pantalla del operador en RTSP 8554
;     para que el decodificador del muro la tome)
;   - regla de firewall para que el decodificador llegue al MediaMTX local.
; Las preferencias del operador viven en %APPDATA%\CLRTrueCentralVMS\client.json
; y no se tocan en las actualizaciones.
;
; Compilación:  installer\build-installers.ps1   (o ISCC /DAppVersion=x.y.z client.iss)
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName       "CLR TrueCentral VMS Client"
#define Publisher     "CLRobotics"
#define ClientExe     "TrueCentralVms.Client.exe"
#define ClientPublish "..\build\publish\client"
#define FlyleafDir    "..\tools\ffmpeg-flyleaf"
#define FfmpegDir     "..\tools\ffmpeg"
#define MediaMtxDir   "..\tools\mediamtx"

[Setup]
AppId={{5E1C2A47-9B3D-4F08-A6E2-7C41D0B9F3A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\CLR TrueCentral VMS\Client
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=CLRTrueCentralVMS-Client-Setup-{#AppVersion}
SetupIconFile=assets\truecentral.ico
UninstallDisplayIcon={app}\{#ClientExe}
UninstallDisplayName={#AppName}
; Identificacion del .exe del instalador. Sin esto, Windows lo muestra como
; "Setup/Uninstall" en el Administrador de tareas y en las propiedades del
; archivo, que no dice nada de que producto es.
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Instalador de {#AppName}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoCopyright=(c) {#Publisher}
VersionInfoOriginalFileName=CLRTrueCentralVMS-Client-Setup.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Si el cliente (o su ffmpeg/mediamtx) está abierto, que el Administrador de
; reinicios lo cierre en vez de fallar la copia:
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el &escritorio"

[InstallDelete]
; Componentes que se reemplazan completos en cada versión (sin restos viejos).
Type: filesandordirs; Name: "{app}\FFmpeg"
Type: filesandordirs; Name: "{app}\tools\ffmpeg"
Type: filesandordirs; Name: "{app}\tools\mediamtx"

[Files]
Source: "{#ClientPublish}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Motor de video: FlyleafLib exige el FFmpeg compartido de SU release, en la
; carpeta FFmpeg\ junto al exe (App.xaml.cs).
Source: "{#FlyleafDir}\*"; DestDir: "{app}\FFmpeg"; Flags: ignoreversion recursesubdirs createallsubdirs
; FFmpeg de línea de comandos: solo lo que usa el cliente (sin ffplay ni docs).
Source: "{#FfmpegDir}\bin\ffmpeg.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "{#FfmpegDir}\bin\ffprobe.exe"; DestDir: "{app}\tools\ffmpeg\bin"; Flags: ignoreversion
Source: "{#FfmpegDir}\LICENSE"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#FfmpegDir}\README.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion skipifsourcedoesntexist
; MediaMTX: sin auto.crt/auto.key (certificados generados localmente; no se
; distribuyen claves privadas — el binario los regenera si hicieran falta).
Source: "{#MediaMtxDir}\mediamtx.exe"; DestDir: "{app}\tools\mediamtx"; Flags: ignoreversion
Source: "{#MediaMtxDir}\mediamtx.yml"; DestDir: "{app}\tools\mediamtx"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#MediaMtxDir}\LICENSE"; DestDir: "{app}\tools\mediamtx"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\CLR TrueCentral VMS\CLR TrueCentral VMS Client"; Filename: "{app}\{#ClientExe}"
Name: "{autodesktop}\CLR TrueCentral VMS Client"; Filename: "{app}\{#ClientExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ClientExe}"; Description: "Abrir CLR TrueCentral VMS Client"; Flags: postinstall nowait skipifsilent

[Code]
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

// Cierra el cliente y las herramientas que lanza (ffmpeg / mediamtx de la
// proyección de pantalla) SOLO si corren desde esta instalación: un MediaMTX
// o ffmpeg de otro programa no se toca.
procedure KillClientTools();
begin
  RunHidden(SysTool('taskkill.exe'), '/F /IM {#ClientExe}');
  RunHidden(SysTool('WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process mediamtx,ffmpeg,ffprobe -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KillClientTools();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS Client MediaMTX"');
    // El decodificador inicia la conexión RTSP hacia este equipo (puerto 8554):
    // sin esta regla, la proyección de pantalla no llega al muro de video.
    RunHidden(SysTool('netsh.exe'),
      'advfirewall firewall add rule name="CLR TrueCentral VMS Client MediaMTX" dir=in action=allow protocol=TCP localport=8554 program="'
      + ExpandConstant('{app}') + '\tools\mediamtx\mediamtx.exe" profile=any');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillClientTools();
    RunHidden(SysTool('netsh.exe'), 'advfirewall firewall delete rule name="CLR TrueCentral VMS Client MediaMTX"');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // Preferencias y registros del operador: no son "datos del sistema" (eso
    // vive en el servidor), pero sí guardan la dirección del servidor, el
    // último usuario y la disposición del muro. Desinstalar el cliente es
    // quitarlo del puesto: no queda nada suyo.
    Settings := ExpandConstant('{userappdata}\CLRTrueCentralVMS');
    DeleteFile(Settings + '\client.json');
    // La carpeta es compartida con el complemento de enrolamiento: se quita
    // solo si queda vacía.
    RemoveDir(Settings);
    // Registros de la proyección de pantalla (ScreenProjection.LogDir).
    DelTree(ExpandConstant('{localappdata}\CLRTrueCentral'), True, True, True);
    // Certificados que MediaMTX se genera solo: no están en el registro de
    // instalación, así que Inno los deja y con ellos la carpeta.
    DelTree(ExpandConstant('{app}\tools'), True, True, True);
  end;
end;
