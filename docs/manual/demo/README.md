# Entorno de demostración para las capturas

Servidor y cliente de TrueCentral con datos ficticios, para tomar las capturas
del manual sin mostrar datos de clientes. No toca el servicio instalado ni el
servidor de desarrollo: usa su propia carpeta, base de datos, MediaMTX y
puertos, y compila el código del último commit (sin cambios locales).

```powershell
.\docs\manual\demo\demo.ps1 Preparar   # compila servidor y cliente desde HEAD (unos minutos)
.\docs\manual\demo\demo.ps1 Iniciar    # cámaras simuladas + servidor
.\docs\manual\demo\demo.ps1 Sembrar    # carga los datos (se puede repetir: no duplica)
.\docs\manual\demo\demo.ps1 Estado
.\docs\manual\demo\demo.ps1 Detener
.\docs\manual\demo\demo.ps1 Borrar     # detiene y borra la base y el período de prueba
```

Panel web: <http://127.0.0.1:5390/>. Usuarios y claves: `demo.json` (son solo
de este entorno local).

## Qué tiene

| Archivo | Para |
|---|---|
| `demo.json` | Puertos, credenciales, cámaras y usuarios de la demo |
| `demo.ps1` | Prepara, inicia, detiene y borra el entorno |
| `camaras.py` | 8 cámaras ONVIF simuladas (video, foto, PTZ y grabaciones) |
| `sembrar.py` | Carga los datos por la API REST, como lo haría una persona |

Datos que siembra: ubicaciones de dos sitios (Casa matriz y Centro de
distribución), 8 cámaras con su ubicación (una PTZ), consignas en tres fichas,
4 roles propios y 5 usuarios (uno deshabilitado), 3 sonidos, 3 automatizaciones
(una pausada) con ejecuciones y alertas (una pendiente), 2 vistas guardadas
compartidas, 2 horarios de acceso y 8 personas con tarjeta.

Carpeta de datos: `%LOCALAPPDATA%\TrueCentralDemo` (código compilado, base,
registros en `logs\`). Se puede borrar entera con el entorno detenido.

| Puerto | Uso |
|---|---|
| 5390 | Panel web y API |
| 8954 | Video hacia los clientes |
| 8670 | Vista en vivo web (WebRTC) |
| 25790, 9951, 9954 | Base de datos y control de video (internos) |
| 8955, 8901–8908 | Cámaras simuladas (RTSP y ONVIF) |

## Capturas

Con el entorno corriendo y sembrado:

```powershell
node docs\manual\demo\capturar-web.mjs                 # todas las del panel web
node docs\manual\demo\capturar-web.mjs auditoria roles # solo las que contengan esos nombres
```

Cada captura se define en `capturas-web/*.mjs`: qué hacer antes (navegar, abrir
menús), qué recortar y qué marcar. Las marcas se miden sobre los elementos
reales y se guardan con su leyenda en `../capturas/marcas.json`; el manual las
lee de ahí (`#captura("archivo.png", pie: […])`), así que al recapturar las
marcas siguen a la interfaz. Si una marca queda fuera de la imagen, la captura
falla en vez de guardarse mal. Opciones útiles: `ancho` / `alto` (otra ventana
para tablas anchas o páginas largas), `reposo` (esperar animaciones) y las
posiciones `izq-fuera` / `der-fuera` para no tapar textos.

El cliente de escritorio tiene su propio capturador (`capturar-cliente/`, UI
Automation más PrintWindow) con las capturas en `capturas-cliente.json`; abre
el cliente de la demo con su configuración aparte (no toca el `client.json` del
cliente instalado) y deja sus ventanas al fondo, detrás de las demás.

Para revisar todas las capturas con sus marcas y leyendas en un solo documento:

```powershell
typst compile docs\manual\revision-capturas.typ revision.pdf
```

## Límites

La demo usa la licencia de prueba de 30 días (todos los módulos con cupos
chicos) y el aviso amarillo de prueba aparece arriba en el panel web: recortarlo
en las capturas.

Sin equipos reales no hay: paneles de alarma, cerco, puertas y niveles de
acceso, citofonía, parlantes, muro de video, patentes, hora de los terminales ni
la línea de tiempo con colores por tipo de grabación (las cámaras ONVIF solo
informan tramos grises). Ver `../capturas/PENDIENTES.md`.
