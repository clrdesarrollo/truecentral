# TrueCentral Speaker — instalación

Convierte una Raspberry Pi o un PC con Windows en un parlante IP de TrueCentral. Diseño completo y etapas:
[docs/diseno/parlante-truecentral.md](../../docs/diseno/parlante-truecentral.md).

Etapa S1 (esta versión): enrolamiento con código, conexión saliente por WebSocket al servidor, identificación,
volumen, tono de prueba, voz en vivo del operador y sonidos del servidor/automatizaciones.

## Raspberry Pi (Raspberry Pi OS de 64 bits)

En el equipo de desarrollo:

```bash
dotnet publish src/TrueCentralVms.SpeakerAgent -c Release -r linux-arm64 --self-contained -o out/speaker-arm64
tar -czf truecentral-speaker-0.5.20-linux-arm64.tar.gz -C out/speaker-arm64 .
```

Copiar el `.tar.gz`, `install-speaker.sh` y `truecentral-speaker.service` a la Pi y:

```bash
sudo ./install-speaker.sh
```

Queda como servicio `truecentral-speaker` (usuario propio, grupo `audio`, datos en `/var/lib/truecentral-speaker`).

## Windows

```powershell
dotnet publish src/TrueCentralVms.SpeakerAgent -c Release -r win-x64 --self-contained -o "C:\Program Files\TrueCentral Speaker"
sc.exe create TrueCentralSpeaker binPath= "\"C:\Program Files\TrueCentral Speaker\truecentral-speaker.exe\"" start= delayed-auto DisplayName= "TrueCentral Speaker"
sc.exe start TrueCentralSpeaker
```

Datos en `%ProgramData%\TrueCentral Speaker` (el token queda cifrado con DPAPI de la máquina). Abrir el puerto
8088 en el firewall solo si se quiere usar la página del equipo desde otro PC. El instalador Inno Setup queda
para la etapa S8.

## Enrolar

1. En TrueCentral: **Parlantes IP → Agregar parlante → TrueCentral Speaker** → Guardar. Aparece un código de
   8 caracteres (vale una vez, 15 minutos).
2. En el equipo, una de dos:
   - página `http://<ip-del-equipo>:8088` (la primera visita define su clave) → servidor y código;
   - consola: `sudo truecentral-speaker enroll --server https://<servidor> --code ABCD-EFGH`
     (en Windows, como administrador: `truecentral-speaker.exe enroll ...`).

El equipo llama al servidor (no hay que abrirle puertos): funciona detrás de NAT, 4G o MPLS. Para reemplazar un
equipo dañado se usa **Reemplazar equipo…** en la fila del parlante; **Revocar** lo desconecta para siempre.

Otros comandos: `truecentral-speaker status`, `truecentral-speaker forget`, `truecentral-speaker version`.

## Hardware de audio en la Pi

Cualquier salida que vea ALSA: jack de la Pi 4, HAT DAC/amplificador (HiFiBerry, IQaudio) o tarjeta USB. Se elige
en la página del equipo (Audio → Salida). Si un HAT no suena, revisar `dtoverlay=` en `/boot/firmware/config.txt`
y probar con `aplay -l`.
