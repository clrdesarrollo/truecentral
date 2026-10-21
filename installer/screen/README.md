# Pantallas de alarma — instalación en la Raspberry Pi

La app de las pantallas de alarma (`src/TrueCentralVms.AlarmScreen`) corre en una
**Raspberry Pi 4 o 5** con **Raspberry Pi OS de 64 bits** (con escritorio) y se conecta
directo al servidor de TrueCentral.

## Paquete

`installer\build-installers.ps1` (compilando la suite) publica la app para linux-arm64,
autocontenida, y deja:

- `dist\truecentral-screen-<versión>-linux-arm64.tar.gz`: para instalar una Pi nueva.
- El mismo archivo dentro de la suite, en `{app}\screen-packages\`: el servidor lo ofrece a
  las Pi ya instaladas (`GET /api/screen/client/version`), que se actualizan solas.

A mano, en el equipo de desarrollo:

```powershell
dotnet publish src\TrueCentralVms.AlarmScreen\TrueCentralVms.AlarmScreen.csproj -c Release -r linux-arm64 --self-contained -o publish-pi
tar -czf truecentral-screen-0.5.19-linux-arm64.tar.gz -C publish-pi .
```

## Instalar una Pi

Copiar a la Pi esta carpeta (`install-screen.sh` y `truecentral-screen-update.sh`) y el
paquete, y ejecutar:

```bash
./install-screen.sh
sudo reboot
```

Deja la app en `~/truecentral-pantalla` como servicio de usuario
(`truecentral-pantalla.service`, se reinicia sola si se cae), el inicio de sesión automático,
la pantalla sin apagado, el permiso para reiniciar desde el panel y la actualización diaria
(04:17, registro en `~/.local/state/truecentral-pantalla/update.log`).

Al arrancar, la Pi busca el servidor por UDP 5094 (no cruza VLANs: si no lo encuentra, en
la misma pantalla se escribe `https://IP`) y muestra un código de 6 dígitos. Se aprueba
en el panel web, **Dispositivos → Pantallas de alarma**. Desde ahí arranca sola.

La configuración y la credencial viven en `~/.config/TrueCentralPantalla/config.json`:
reinstalar la app no obliga a volver a aprobarla. Si se pierde (SD nueva), en el panel:
**Reiniciar emparejamiento** y aprobar el código nuevo.

## Actualización remota

`truecentral-screen-update.sh` consulta la versión publicada con la credencial de la
pantalla (solo al servidor cuya llave fijó al emparejarse), descarga el paquete con su
manifiesto firmado, reemplaza la carpeta de la app y, si la versión nueva no levanta en 10
segundos, vuelve a la anterior.

**Firma.** Antes de instalar, verifica con `openssl` la firma Ed25519 del manifiesto
(producto, versión, archivo y SHA-256 del paquete) contra la llave pública de CLRobotics que
trae el propio script (`UPDATE_PUBKEY`). La firma se hace al compilar
(`installer\build-installers.ps1`, con la llave privada de la credencial de Jenkins
`truecentral-update-signing-key` o `installer\config\update-signing.key`), así que ni un
servidor comprometido puede hacer que una pantalla instale algo que no salió de CLRobotics.
Sin firma válida no se instala nada, y nunca se instala una versión igual o anterior a la
que tiene.

La llave se genera con `installer\new-update-signing-key.ps1`. Si hubiera que cambiarla
(la privada se filtró), hay que poner la pública nueva en `UPDATE_PUBKEY` y volver a correr
`install-screen.sh` en cada pantalla: el actualizador no se actualiza a sí mismo.
