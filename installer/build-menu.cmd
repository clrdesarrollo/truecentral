@echo off
rem Menu para compilar instaladores (Terminal.Gui). Sin argumentos abre el menu;
rem con argumentos los pasa tal cual (ver: build-menu.cmd --help).
rem   Modo silencioso (IA / scripts):  build-menu.cmd --silent --only suite,client
dotnet run --project "%~dp0BuildTool\CLRBuild.csproj" -c Release --nologo -v q -- %*
exit /b %ERRORLEVEL%
