# Nodo Jenkins en Windows — CLR TrueCentral VMS

Cómo dejar operativo un agente Windows capaz de ejecutar el `Jenkinsfile` de la
raíz. El **controlador** (la UI de Jenkins) no compila nada: solo reparte
trabajo. El **nodo** es la máquina Windows x64 que hace el build y produce los
dos `.zip` de la entrega.

Todo lo de aquí se hace en la máquina que será el nodo, salvo el paso 5, que es
en la UI del controlador.

> No compila en Linux: el cliente es WPF y los SDK de Hikvision/Dahua son
> nativos de 64 bits.

---

## 1. Requisitos del nodo

| Componente | Para qué | Cómo |
|---|---|---|
| Windows 10/11 x64 o Server 2019/2022 | WPF + SDK nativos x64 | — |
| **Java 17 o 21** (JRE) | `agent.jar` corre sobre la JVM. Jenkins 2.479+ ya no arranca con Java 11 | ver §2 |
| **.NET SDK 10** | `dotnet restore` / `build` / `publish` | `winget install Microsoft.DotNet.SDK.10` |
| **Git for Windows** | `checkout scm` y el `git ls-files` de la etapa de higiene | `winget install Git.Git` |
| `tar.exe`, `robocopy`, `powershell.exe` | Empaquetado y copia de binarios | ya vienen con Windows |
| Salida a `nuget.org` | `restore` y la etapa de auditoría (`RUN_AUDIT`) | firewall / proxy |

Conviene una **cuenta local dedicada** (por ejemplo `jenkins`) con **perfil de
usuario real**, no `LocalSystem`: `dotnet` escribe en `%USERPROFILE%\.nuget` y
en `%LOCALAPPDATA%`.

Comprobación, en una consola nueva y como esa cuenta:

```powershell
java -version; dotnet --info; git --version; where.exe tar robocopy
```

---

## 2. De dónde sacar Java

Para el agente basta el **JRE**; el JDK también sirve, solo pesa más.

**Recomendado — Eclipse Temurin 21 (JRE):**
<https://adoptium.net/temurin/releases/?version=21&os=windows&arch=x64&package=jre>

Descargue el `.msi` de Windows x64 · JRE · 21 (LTS). En la pantalla *Custom
Setup* del instalador active **Set JAVA_HOME variable** y **Add to PATH** (vienen
desactivados). Sin eso hay que poner la ruta completa al `java.exe` a mano.

Por línea de comandos, en una consola de administrador:

```powershell
winget install --id EclipseAdoptium.Temurin.21.JRE --silent --accept-source-agreements --accept-package-agreements
```

Alternativas equivalentes: Microsoft Build of OpenJDK 21
(`winget install Microsoft.OpenJDK.21`, solo entrega JDK), Amazon Corretto 21 o
Azul Zulu 21. **No** use el JRE de Oracle (`java.com`): licencia comercial
restrictiva para uso en servidores y no aporta nada aquí.

Verifique y anote la ruta — es la que va en el XML del servicio (§7):

```powershell
java -version; (Get-Command java).Source
```

Si el nodo ya tenía otro Java instalado (típico: un Java 8 de algún software de
cámaras), `where.exe java` dirá cuál gana en el PATH. En ese caso use la ruta
absoluta del Temurin en el XML y no dependa del PATH.

---

## 3. Carpetas y ajustes del sistema

```powershell
New-Item -ItemType Directory -Force C:\CI\jenkins-agent, C:\CI\truecentral-binaries
```

- `C:\CI\jenkins-agent` — *remote root directory* del nodo (workspaces).
- `C:\CI\truecentral-binaries` — es el valor literal de `BINARIES_DIR` en el
  `Jenkinsfile`. Si lo pone en otra ruta, cámbielo en la configuración del job.

Rutas cortas a propósito: el publish de .NET genera rutas profundas. Además:

```powershell
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' LongPathsEnabled 1
git config --system core.longpaths true
Add-MpPreference -ExclusionPath C:\CI
```

La exclusión de Defender sobre el workspace ahorra varios minutos por build.

---

## 4. Poblar `BINARIES_DIR` (una sola vez)

`native\` y `tools\` no viven en git (cientos de MB de redistribuibles). Se
generan en una máquina de desarrollo y se dejan fijos en el nodo. Desde un
checkout local:

```powershell
.\build\setup-binaries.ps1 -FromProduct "C:\ruta\a\otro\checkout"
```

Cuando el script reporte todo `[OK]`, copie las dos carpetas al nodo:

```powershell
robocopy .\native \\NODO\C$\CI\truecentral-binaries\native /E
robocopy .\tools  \\NODO\C$\CI\truecentral-binaries\tools  /E
```

El nodo debe quedar al menos con esto, que es lo que el pipeline verifica:

```
C:\CI\truecentral-binaries\native\hikvision\HCNetSDK.dll
C:\CI\truecentral-binaries\native\dahua\dhnetsdk.dll
C:\CI\truecentral-binaries\tools\postgres\pgsql\bin\pg_ctl.exe
C:\CI\truecentral-binaries\tools\mediamtx\mediamtx.exe
C:\CI\truecentral-binaries\tools\ffmpeg\bin\ffmpeg.exe
C:\CI\truecentral-binaries\tools\ffmpeg-flyleaf\avcodec-*.dll
```

Sin esa carpeta el build compila igual, pero los `.zip` salen incompletos y la
ejecución queda marcada **UNSTABLE**.

---

## 5. Crear el nodo en el controlador

*Manage Jenkins → Nodes → New Node*, tipo **Permanent Agent**:

| Campo | Valor |
|---|---|
| Node name | `win-build-01` |
| Remote root directory | `C:\CI\jenkins-agent` |
| **Labels** | **`windows`** — obligatorio, es lo que pide `agent { label 'windows' }` |
| Usage | *Use this node as much as possible* |
| Number of executors | `1` — dos MSBuild simultáneos se pisan en `obj\` |
| Launch method | *Launch agent by connecting it to the controller* (inbound) |
| Availability | *Keep this agent online as much as possible* |

Opcional, en *Node Properties → Environment variables*: `BINARIES_DIR`, si en
este nodo los binarios viven en otra ruta.

Si usa inbound **sin** WebSocket, habilite antes el puerto en *Manage Jenkins →
Security → TCP port for inbound agents → Fixed* (por ejemplo `50000`) y ábralo
en el firewall. Con `-webSocket` —recomendado si el controlador está detrás de
HTTPS o un reverse proxy— ese puerto no hace falta.

Al guardar, la página del nodo muestra el `secret` y el comando de conexión.

---

## 6. Primera conexión (prueba manual)

En el nodo, baje `agent.jar` del propio controlador y guarde el secret:

```powershell
Invoke-WebRequest http://TU-JENKINS:8080/jnlpJars/agent.jar -OutFile C:\CI\jenkins-agent\agent.jar
Set-Content C:\CI\jenkins-agent\secret-file -Value 'EL_SECRET_DEL_NODO' -NoNewline
```

```powershell
java -Dfile.encoding=UTF-8 -jar C:\CI\jenkins-agent\agent.jar -url http://TU-JENKINS:8080/ -secret "@C:\CI\jenkins-agent\secret-file" -name win-build-01 -webSocket -workDir C:\CI\jenkins-agent
```

Debe imprimir `INFO: Connected` y el nodo aparecer en línea. Corte con `Ctrl+C`
y siga al paso 7.

> El `-Dfile.encoding=UTF-8` no es decorativo. Jenkins escribe cada bloque
> PowerShell del pipeline a un `.ps1` temporal; si el agente no lo maneja como
> UTF-8, los caracteres acentuados se decodifican mal y revientan el parser de
> PowerShell. Es el mismo motivo por el que los bloques del `Jenkinsfile` van
> sin tildes.

---

## 7. Instalarlo como servicio de Windows (WinSW)

Para que sobreviva a reinicios y no dependa de una sesión abierta:

```powershell
Invoke-WebRequest https://github.com/winsw/winsw/releases/latest/download/WinSW-x64.exe -OutFile C:\CI\jenkins-agent\jenkins-agent.exe
```

`C:\CI\jenkins-agent\jenkins-agent.xml`:

```xml
<service>
  <id>jenkins-agent</id>
  <name>Jenkins Agent (win-build-01)</name>
  <description>Agente inbound de Jenkins - CLR TrueCentral VMS</description>
  <executable>C:\Program Files\Eclipse Adoptium\jre-21-hotspot\bin\java.exe</executable>
  <arguments>-Dfile.encoding=UTF-8 -Dsun.jnu.encoding=UTF-8 -jar "C:\CI\jenkins-agent\agent.jar" -url http://TU-JENKINS:8080/ -secret @"C:\CI\jenkins-agent\secret-file" -name "win-build-01" -webSocket -workDir "C:\CI\jenkins-agent"</arguments>
  <workingdirectory>C:\CI\jenkins-agent</workingdirectory>
  <logmode>rotate</logmode>
  <onfailure action="restart" delay="10 sec"/>
  <startmode>Automatic</startmode>
</service>
```

Ajuste `<executable>` a la ruta real que devolvió `(Get-Command java).Source`.
Instale y arranque:

```powershell
C:\CI\jenkins-agent\jenkins-agent.exe install
Start-Service jenkins-agent
```

**La cuenta del servicio se cambia a mano**, no en el XML: `services.msc →
Jenkins Agent → Iniciar sesión → Esta cuenta → .\jenkins`. Así la contraseña no
queda en texto plano en un archivo. Reinicie el servicio después.

La carpeta contiene el secret del nodo, conviene cerrarla:

```powershell
icacls C:\CI\jenkins-agent /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administradores:(OI)(CI)F" "jenkins:(OI)(CI)F"
```

---

## 8. Credenciales del repositorio

El remoto es `git@github.com:clrdesarrollo/truecentral.git`. La clave SSH va en
el **controlador** (*Manage Jenkins → Credentials → SSH Username with private
key*, usuario `git`), no en el nodo: Jenkins se la inyecta al agente en cada
`checkout scm`.

En *Manage Jenkins → Security → Git Host Key Verification Configuration* elija
*Known hosts file* y siembre `known_hosts` con la clave de github.com, o
*Accept first connection* si prefiere. Alternativa: clonar por HTTPS con un PAT
como credencial *Username with password*.

---

## 9. Verificación

Cree el job (*New Item → Multibranch Pipeline* apuntando al repo, script
`Jenkinsfile`) y lance un build. El nodo quedó bien si:

- **Binarios de terceros** imprime `OK native (…MB)` y `OK tools (…MB)`, y no
  el aviso de que faltan.
- **Compilar** termina sin errores → el SDK de .NET 10 está en el PATH del
  servicio.
- **Auditoría de dependencias** dice `OK: sin paquetes vulnerables` → el nodo
  llega a nuget.org.
- **Empaquetar** lista los dos `.zip` y el build queda verde, no UNSTABLE.

---

## 10. Fallas típicas

| Síntoma | Causa |
|---|---|
| `dotnet: no se reconoce` | El servicio arrancó antes de instalar el SDK, o el PATH se agregó al usuario y no a la máquina. Reinicie el servicio. |
| UNSTABLE con "paquetes armados SIN los binarios de terceros" | `BINARIES_DIR` vacío o apuntando a otra ruta. |
| El agente conecta y se cae en bucle | Java incompatible con el controlador, o `agent.jar` desactualizado: vuelva a bajarlo de `/jnlpJars/agent.jar`. |
| Errores de parser de PowerShell con comillas raras | Falta `-Dfile.encoding=UTF-8` en los argumentos del servicio. |
| `robocopy fallo con codigo 8+` | Permisos: la cuenta del servicio no lee `C:\CI\truecentral-binaries`. |
| Fallas de ruta larga en el publish | `LongPathsEnabled` y `core.longpaths` sin activar. |
| `winget` se cuelga y no instala | No pudo actualizar su índice (red/proxy). Use el `.msi` de Adoptium directamente. |
