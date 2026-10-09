# Manual de usuario

Manual de CLR TrueCentral VMS escrito en [Typst](https://typst.app). El PDF se
genera desde estos archivos; no se edita a mano.

## Compilar

```powershell
.\docs\manual\build-manual.ps1                          # dist\CLRTrueCentralVMS-Manual-<versión>.pdf
.\docs\manual\build-manual.ps1 -Watch                   # recompila al guardar (para escribir)
.\docs\manual\build-manual.ps1 -Modulos video,playback  # edición con solo esos módulos
.\docs\manual\build-manual.ps1 -Final                   # entrega: falla si queda algo pendiente
.\docs\manual\build-manual.ps1 -RevisarReferencias      # cada edición sin un módulo: falla si queda
                                                        # una referencia a un capítulo ausente
```

Correr `-RevisarReferencias` después de agregar referencias entre capítulos.

Typst se instala con `winget install --id Typst.Typst -e`. Un capítulo suelto
también compila (sin el diseño completo), útil para revisar errores:
`typst compile --root docs\manual docs\manual\capitulos\04-reproduccion.typ salida.pdf`
(`--root` hace falta porque el capítulo importa `../plantilla.typ`).

## Archivos

| Archivo | Contenido |
|---|---|
| `manual.typ` | Documento principal: partes, orden de los capítulos y módulo de licencia de cada uno |
| `plantilla.typ` | Diseño y componentes. Los capítulos no definen estilos propios |
| `capitulos/NN-tema.typ` | Un capítulo por archivo |
| `capitulos/aN-tema.typ` | Apéndices (atajos; puertos y ajustes del servidor) |
| `capturas/` | Imágenes de pantalla (PNG) |
| `capturas/PENDIENTES.md` | Capturas que faltan: archivo, pantalla, estado y marcas |
| `build-manual.ps1` | Compila con la versión del archivo `VERSION` |

## Agregar un capítulo

1. Crear `capitulos/NN-tema.typ` empezando con:
   ```typst
   #import "../plantilla.typ": *
   // Fuentes: src/TrueCentralVms.Client/Views/XxxView.xaml, src/TrueCentralVms.Server/wwwroot/xxx.js

   = Título del capítulo <cap-tema>
   ```
   La línea `// Fuentes:` lista el código que describe el capítulo: cuando esas
   pantallas cambien, se sabe qué capítulo revisar.
2. Si el capítulo menciona atajos de teclado o ajustes de `appsettings.Local.json`,
   sumarlos también al apéndice que corresponde.
3. Incluirlo en `manual.typ`. Si el módulo se vende aparte, envolverlo en
   `#modulo("clave", include "…")` con la clave de `LicenseFeatures` sin el
   prefijo `module_` (video, playback, anpr, alarms, access, videowall,
   speakers, intercom, automation).

## Componentes

| Escribir | Para |
|---|---|
| `#boton("Exportar")` | Botones y controles que se hacen clic |
| `*Servidor*` | Nombre de un campo, casilla o columna, tal como aparece en pantalla |
| `#menu("Seguridad", "Roles")` | Ruta del menú lateral del panel web |
| `#tecla("Esc")` | Teclas |
| `#nota[…]` `#consejo[…]` `#importante[…]` | Avisos (con moderación: uno o dos por sección) |
| `+ paso` | Pasos numerados de un procedimiento |
| `- elemento` | Listas sin orden |
| `#table(…)` | Referencia: atajos, estados, colores, columnas |
| `#requiere("Ver cercos eléctricos")` | Permisos que exige la función, con el nombre de `Permissions.cs` |
| `#captura("archivo.png", marcas: ((x, y), …), pie: […])` | Captura con marcas numeradas (x, y entre 0 y 1) |
| `#marca(1)` | Referencia en el texto a la marca 1 de la captura |
| `#captura-pendiente("…")` | Captura que todavía no existe: describir la pantalla y su estado |
| `#capitulo(<cap-reproduccion>)` | Referencia a otro capítulo o apéndice (sale "el capítulo 4, _Reproducción_") |
| `#pendiente[…]` | Contenido por escribir |
| `#modulo("speakers")[…]` | Párrafo que solo va si la edición trae el módulo |

`-Final` hace fallar la compilación si queda algún `#pendiente`,
`#captura-pendiente` o `#capitulo` cuyo destino no existe. Una referencia a un
capítulo de otro módulo se envuelve en `#modulo(…)` para que las ediciones
sin ese módulo no queden con la referencia colgando.

## Estilo de redacción

- Español de Chile, trato de **usted**, verbos en imperativo en los pasos
  ("Haga clic en…", "Seleccione…", "Escriba…").
- Se escribe para quien opera, no para quien programa: qué se puede hacer y
  cómo, no cómo funciona por dentro. Nada de nombres internos (MediaMTX, SDK,
  ISAPI, endpoints, tablas, clases) salvo que el usuario los vea en pantalla o
  los necesite para configurar algo.
- Los nombres de botones, campos, menús y mensajes se copian **exactamente**
  como aparecen en la interfaz (XAML del cliente, HTML/JS del panel web).
- Cada afirmación sale del código actual. Lo que no se pudo confirmar no se
  escribe. Las funciones marcadas "Próximamente" no se documentan.
- Un capítulo por módulo: primero el uso diario (operación) y al final la
  configuración para administradores.
- Secciones por tarea ("Exportar un tramo", "Armar una partición"), no por
  pantalla. Oraciones cortas; un paso = una acción.
- Los íconos sin texto se nombran por su descripción emergente o su forma
  ("el botón de engranaje *Configuración*"); los glifos de Segoe MDL2 no se
  pueden escribir en el PDF.

## Typst: cuidado con estos caracteres

En el texto, `#`, `$`, `*`, `_`, `@`, `<`, `` ` `` y `\` tienen significado.
Para escribirlos literalmente se anteponen con `\` (`\#`, `\$`, `\@`) o se
ponen entre comillas invertidas como código (`` `C:\Program Files` ``).

## Capturas

- Nombre: `<superficie>-<pantalla>[-detalle].png` (`web-inicio-sesion.png`,
  `cliente-vista-en-vivo-ptz.png`).
- Panel web: Edge sin ventana a 1280×800 con escala 2 y recorte a la zona que
  interesa. Las marcas se ubican como fracción del ancho y alto de la imagen,
  así que recapturar con el mismo encuadre no obliga a moverlas.
- Datos de las capturas: solo de un entorno de demostración, nunca personas,
  patentes ni direcciones reales de clientes.
