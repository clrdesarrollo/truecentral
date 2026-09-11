; ============================================================================
; CLR TrueCentral VMS — Instalador del COMPLEMENTO DE ENROLAMIENTO (Inno Setup 6)
; ----------------------------------------------------------------------------
; Se instala en el PC donde está conectado el LECTOR DE HUELLAS USB (Hikvision
; DS-K1F820-F / DS-K1F800-F), que normalmente es el puesto de RR.HH. o de
; portería, no el servidor.
;
; Por qué existe: el panel web no puede hablar con el hardware del equipo del
; operador. El complemento corre en la bandeja del sistema, expone su API SOLO
; en 127.0.0.1 y el panel lo descubre y le pide las capturas.
;
; A diferencia del cliente y de la suite, este NO pide elevación: escucha en
; loopback con Kestrel (sin urlacl) y abre el lector con permisos de usuario,
; así puede arrancar con la sesión sin avisos de UAC. Por lo mismo se instala
; para el usuario que lo ejecuta.
;
; Compilación:  installer\build-installers.ps1   (o ISCC /DAppVersion=x.y.z complemento.iss)
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName    "CLR TrueCentral VMS — Complemento de enrolamiento"
#define ShortName  "CLR TrueCentral VMS Complemento"
#define Publisher  "CLRobotics"
#define AgentExe   "TrueCentralVms.WebControl.exe"
#define AgentDir   "..\build\publish\complemento"

[Setup]
AppId={{B7F4C21E-6A83-4D95-9C07-2E5A8F31D6B4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
; Instalación por usuario: no necesita administrador (ver encabezado).
PrivilegesRequired=lowest
DefaultDirName={autopf}\CLR TrueCentral VMS\Complemento
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=CLRTrueCentralVMS-Complemento-Setup-{#AppVersion}
SetupIconFile=assets\truecentral.ico
UninstallDisplayIcon={app}\{#AgentExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=Instalador de {#AppName}
VersionInfoProductName={#AppName}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoCopyright=(c) {#Publisher}
VersionInfoOriginalFileName=CLRTrueCentralVMS-Complemento-Setup.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Si el complemento está corriendo, que el Administrador de reinicios lo cierre
; en vez de fallar la copia.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startup"; Description: "Iniciar el complemento junto con Windows"; GroupDescription: "Al iniciar sesión:"

[Files]
; El publish es self-contained: el puesto no necesita tener .NET instalado.
; FPModule_SDK.dll (el SDK del lector) va adentro del publish.
Source: "{#AgentDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CLR TrueCentral VMS\{#ShortName}"; Filename: "{app}\{#AgentExe}"
; Arranque con la sesión: es lo normal, porque el panel espera encontrarlo
; corriendo cuando alguien va a enrolar una huella.
Name: "{userstartup}\{#ShortName}"; Filename: "{app}\{#AgentExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AgentExe}"; Description: "Iniciar el complemento ahora"; Flags: postinstall nowait skipifsilent

[Code]
function RunHidden(const FileName, Params: string): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode;
end;

// El complemento mantiene tomado el lector y su puerto: hay que cerrarlo antes
// de reemplazar los archivos, y al desinstalar.
procedure KillAgent();
begin
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AgentExe}');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KillAgent();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings: string;
begin
  if CurUninstallStep = usUninstall then KillAgent();

  if CurUninstallStep = usPostUninstall then
  begin
    // Ajustes del lector de este puesto. La carpeta la comparte con el cliente
    // de escritorio (client.json), así que solo se quita si queda vacía.
    Settings := ExpandConstant('{userappdata}\CLRTrueCentralVMS');
    DeleteFile(Settings + '\web-control.json');
    RemoveDir(Settings);
  end;
end;
