# Manual de usuario

Manual de CLR TrueCentral VMS escrito en [Typst](https://typst.app). El PDF y
el sitio web se generan desde estos mismos archivos; ninguno de los dos se
edita a mano.

## Compilar

```powershell
.\docs\manual\build-manual.ps1                          # dist\CLRTrueCentralVMS-Manual-<versión>.pdf
.\docs\manual\build-manual.ps1 -Web                     # además el sitio: dist\CLRTrueCentralVMS-Manual-<versión>-web\
.\docs\manual\build-manual.ps1 -Watch                   # recompila al guardar (para escribir)
.\docs\manual\build-manual.ps1 -Watch -Web              # el sitio en http://localhost:3000, recargado al guardar
.\docs\manual\build-manual.ps1 -Modulos video,playback  # edición con solo esos módulos (también con -Web)
.\docs\manual\build-manual.ps1 -Final                   # entrega: falla si queda algo pendiente
.\docs\manual\build-manual.ps1 -RevisarReferencias      # cada edición sin un módulo: falla si queda
                                                        # una referencia a un capítulo ausente
```

Correr `-RevisarReferencias` después de agregar referencias entre capítulos.

Typst se instala con `winget install --id Typst.Typst -e`. Un capítulo suelto
también compila (sin el diseño completo), útil para revisar errores:
`typst compile --root docs\manual docs\manual\capitulos\04-reproduccion.typ salida.pdf`
(`--root` hace falta porque el capítulo importa `../plantilla.typ`).

## Sitio web

`-Web` genera, junto al PDF, una carpeta con el manual como sitio: una portada
(`index.html`), una página por capítulo con el nombre de su archivo
(`04-reproduccion.html`), las capturas que usa la edición, la hoja de estilos y
una copia del PDF para el botón *Descargar PDF*. Tiene índice lateral, anterior
y siguiente, búsqueda en todo el manual (con o sin tildes; `/` o
`Ctrl`+`K`), tema claro y oscuro según el equipo, y se adapta al teléfono. No
usa nada de Internet.

- Cada sección tiene un ancla fija tomada de su título:
  `04-reproduccion.html#exportar-un-tramo`. Sirve para enlazar la ayuda desde la
  aplicación; cambiar el título de una sección cambia su ancla.
- La búsqueda lee las páginas de los capítulos, así que necesita el sitio
  servido por HTTP (el servidor o `-Watch -Web`). Abierto con doble clic sobre
  `index.html` busca solo en los títulos de los capítulos.
- Usa la exportación HTML y *bundle* de Typst, que todavía es experimental:
  puede cambiar de una versión de Typst a otra. Si al actualizar Typst el sitio
  sale distinto, revisar `manual-web.typ` y los componentes de `plantilla.typ`.
  El PDF no depende de esas funciones.

Los capítulos se escriben igual para las dos salidas. Cada componente de
`plantilla.typ` decide su dibujo según la salida (`target()`): en el PDF lo
dibuja Typst; en la web emite elementos HTML con las clases de
`web/manual.css`. Por eso los capítulos usan solo los componentes de abajo y el
marcado normal de Typst (títulos, listas, tablas, negrita, código): el diseño
de página de Typst (`box`, `grid`, `place`, `v`, colores de texto) no pasa al
HTML. Un componente nuevo necesita sus dos versiones y sus estilos en
`web/manual.css`.

## Archivos

| Archivo | Contenido |
|---|---|
| `estructura.typ` | Partes, orden de los capítulos y módulo de licencia de cada uno. Lo leen el PDF y el sitio |
| `manual.typ` | Documento PDF: portada, índice, partes y apéndices |
| `manual-web.typ` | Sitio web: portada, una página por capítulo, índice lateral |
| `plantilla.typ` | Diseño y componentes, con su versión PDF y su versión HTML. Los capítulos no definen estilos propios |
| `web/` | Hoja de estilos, script (índice y búsqueda) e ícono del sitio |
| `capitulos/NN-tema.typ` | Un capítulo por archivo |
| `capitulos/aN-tema.typ` | Apéndices (atajos; puertos y ajustes del servidor) |
| `capturas/` | Imágenes de pantalla (PNG) |
| `capturas/PENDIENTES.md` | Capturas que faltan: archivo, pantalla, estado y marcas |
| `build-manual.ps1` | Compila el PDF y, con `-Web`, el sitio, con la versión del archivo `VERSION` |

## Agregar un capítulo

1. Crear `capitulos/NN-tema.typ` empezando con:
   ```typst
   #import "../plantilla.typ": *
   // Fuentes: src/TrueCentralVms.Client/Views/XxxView.xaml, src/TrueCentralVms.Server/wwwroot/xxx.js

   = Título del capítulo <cap-tema>
   ```
   La línea `// Fuentes:` lista el código que describe el capítulo: cuando esas
   pantallas cambien, se sabe qué capítulo revisar. El título va en una sola
   línea con su etiqueta, como en el ejemplo: el sitio web lo lee de ahí para
   el índice y el nombre de la página.
2. Si el capítulo menciona atajos de teclado o ajustes de `appsettings.Local.json`,
   sumarlos también al apéndice que corresponde.
3. Agregarlo en `estructura.typ`, en su parte y en el orden de lectura:
   `(archivo: "NN-tema")`. Si el módulo se vende aparte, con su módulo:
   `(archivo: "NN-tema", modulo: "clave")`, con la clave de `LicenseFeatures`
   sin el prefijo `module_` (video, playback, anpr, alarms, access, videowall,
   speakers, io, alarm_screens, intercom, automation). Sale en el PDF y en el
   sitio a la vez.

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
