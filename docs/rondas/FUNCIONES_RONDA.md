# Funciones de la app Ronda (Qwantec, v4.0)

Inventario para diseñar la app nueva. Fuentes:
- **Manual**: `RondaManual.pdf`, rev. 1.2.
- **Código**: `Ronda.exe` (C++Builder 5, base de datos Firebird 1.5 `RONDA.IBD`), con sus 50 formularios
  y unas 100 consultas SQL extraídas del ejecutable.

Leyenda: **[M]** lo describe el manual · **[C]** se ve en el código pero no en el manual ·
**[I]** inferido, sin confirmar del todo.

## 1. Conceptos

| Concepto | Qué es | Tabla |
|---|---|---|
| **Captivador / lector** | El bastón (Captor 4). Lee tags de proximidad y guarda hasta 60.000 lecturas. | `CAPTOR`, `SET_CAPTOR` |
| **CheckPoint** | Tag fijo en un lugar a vigilar. Tiene código, ubicación e *Id Botón* (número de serie hex). | `CHECK_POINT` |
| **Identificador / tarjeta** | Tag (llavero o tarjeta) de un guardia. Uno por guardia; si está asignado aparece como «ocupado». | `TARJETA` |
| **Guardia** | Vigilante. Su clave es el RUT. | `GUARDIA` |
| **Ruta** | Secuencia ordenada de checkpoints con tiempos objetivo. | `RUTA`, `RUTA_CHECK_POINT` |
| **Calendarización** | Qué rutas se hacen, qué días y a qué hora empiezan. | `PROGRAMACION_RUTA` |
| **Bitácora** | Cada paso de un guardia por un checkpoint: fecha-hora, guardia y checkpoint. | `BITACORA` |

**Uso en terreno [M]:** el guardia acerca el lector al checkpoint (1 vibración + luz) y **a continuación
lee su propio identificador**. Así cada paso queda como dos lecturas seguidas: checkpoint y guardia.

## 2. Funciones por menú

### Acceso y usuarios
- **Login** con usuario y contraseña. Usuario por defecto `admin`/`admin` [M].
- **Administrador de usuarios** (solo administradores): crear, modificar y eliminar usuarios [M].
- **Tipos de usuario** configurables (`TIPO_USUARIO`), en la práctica «administrador» y «normal» [M][C].
- La tabla `USUARIO` tiene un campo `software`: la misma base sirve para varios productos de Qwantec [C].
- Las contraseñas se guardan **en claro** y se comparan directamente en el SQL
  (`WHERE nombre = :nombre AND clave = :clave`) [C].

### Identificadores de guardias (`FormTarjetas`)
- Alta, edición y baja de identificadores: código libre + Id Botón [M].
- **«Obtener ID»**: lee del bastón el último tag leído para no teclearlo [M].
- Muestra qué identificadores están libres y cuáles ocupados (`disponible`) [M][C].
- No deja borrar un identificador asignado a un guardia, ni usar un Id Botón que ya pertenece a un checkpoint [C].

### Guardias (`FormGuardia`, `FormNuevoGuardia`, `FormModificarGuardia`)
- Lista con búsqueda por código y por nombre [M].
- Agregar, editar y eliminar (con confirmación) [M].
- Ficha: RUT, nombres, apellidos, sexo, edad, fecha de nacimiento, estado civil, teléfono, dirección,
  comuna, provincia, región e identificador asignado [C].
- «Imprimir datos» de un guardia (`FormGuardiaQR`) [M].

### CheckPoints (`FormCheckPoints`)
- Alta, edición y baja: código, ubicación e Id Botón. El Id se puede teclear (con «00» delante) o
  leer del bastón con «Obtener ID» [M].
- Los checkpoints desconocidos que llegan en una descarga se dan de alta automáticamente con
  nombre «SECTOR n», para renombrarlos después [C][I].

### Rutas (`FormRutas`, `FormRutaCheckPoint`)
- Crear, renombrar y eliminar rutas [M].
- Añadir checkpoints a una ruta, con:
  - **secuencia**: orden; el 0 inicia la ruta.
  - **tiempo**: minutos desde el inicio de la ruta.
  - **error**: margen en minutos (± sobre el tiempo).
- Reordenar con flechas, modificar y quitar checkpoints de la ruta sin borrarlos del sistema [M].

### Calendarización de rutas (`FormCalendarizacionDeRutas`)
- Para cada ruta: hora de inicio y días de la semana. Se pueden poner varias horas por ruta [M][C].
- El cumplimiento se comprueba con esta programación [M].

### Bitácora guardia (`FormBitacora`)
- Ver, para una fecha, los pasos de cada guardia por los checkpoints, con búsqueda por código o nombre [M].
- Solo administradores: **corregir el guardia** de un registro (p. ej. si usó la tarjeta de otro) y
  **eliminar** registros incorrectos [M].

### Descarga (`FormDescarga`, `FormProgresoDescarga`)
- Instrucciones paso a paso y botón «Descargar» con confirmación y barra de progreso [M].
- Configuración del puerto serie, solo para captivadores antiguos (Captor 2 y 3) [M].
- Proceso observado en el código [C]:
  1. Conecta, lee el número del captivador y **comprueba su licencia** (ver §4).
  2. No permite descargar si no hay guardias registrados.
  3. **Pone en hora el bastón** con la hora del PC. Rechaza la descarga si el bastón tiene una
     sincronización posterior a la hora actual (PC con la fecha mal).
  4. Descarga las lecturas y **borra la memoria** del bastón.
  5. Recorre las lecturas: si el tag es un checkpoint, lo recuerda; si es una tarjeta, busca su guardia
     (`???` si la tarjeta no tiene guardia); si es desconocido, la lectura se ignora.
     Cada pareja checkpoint → guardia se guarda en `BITACORA` [C][I].
  6. Informa de cuántos registros se guardaron y cuántos no se pudieron ingresar.
- Al **descargar un bastón nuevo por primera vez**, los checkpoints y las tarjetas del kit se dan de alta
  automáticamente [M].

### Informes (todos imprimibles con QuickReport)
| Informe | Parámetros | Contenido |
|---|---|---|
| Lista de guardias | — | código, nombre, identificador [M] |
| Lista de checkpoints | — | código, ubicación, Id Botón [M] |
| General de rondas | desde/hasta | todos los pasos: fecha-hora, guardia, checkpoint [M] |
| Rondas por guardia | desde/hasta + guardia(s) | pasos de cada guardia; impresión en lote de varios guardias [M] |
| Frecuencias por checkpoint | desde/hasta | nº de pasos por checkpoint y tiempo mínimo y máximo entre pasos (procedimiento `FRECUENCIAS_CHECK_POINT`) [M][C] |
| Por checkpoint | desde/hasta + checkpoint | qué guardias pasaron por él [M] |
| **Cumplimiento de rutas** | ruta(s), guardia(s), periodo | rutas que debían hacerse según la calendarización; detalle por checkpoint: hora esperada, margen, hora real y estado [M] |
| Calendarización / programación de rutas | — | listado de la programación (`FormInformeCalendarizacionRutas`, `FormInformeProgramacionRuta`) [C] |

El informe de cumplimiento genera el detalle en una tabla temporal (`DETALLE_CP_TEMP`).

### Configuración
- `conf.ini`: base de datos, modelo de captivador (`Captor2/3/4`) y nombre de la entidad vigilada
  («Guardia»/«Guardias»), que se usa en los textos de la interfaz [C].

## 3. Modelo de datos (reconstruido del SQL)

```
USUARIO(nombre, software, clave, codigo_tipo_usuario → TIPO_USUARIO)
TIPO_USUARIO(codigo, nombre)
GUARDIA(rut PK, nombres, apellidos, sexo, edad, fecha_nacimiento, estado_civil,
        telefono, direccion, comuna, provincia, region, codigo_tarjeta → TARJETA)
TARJETA(codigo PK, id_boton, disponible)
CHECK_POINT(codigo PK, ubicacion, id_boton)
RUTA(codigo PK, nombre)
RUTA_CHECK_POINT(codigo_ruta → RUTA, codigo_check_point → CHECK_POINT, secuencia, tiempo, error)
PROGRAMACION_RUTA(codigo_ruta → RUTA, dia, hora_inicio)
BITACORA(fecha_hora, rut_guardia → GUARDIA, codigo_check_point → CHECK_POINT)
CAPTOR(serial, fecha_ult_sinc, seg_ult_sinc, tmr_ult_sinc, estado, dias_restantes)
SET_CAPTOR(serial, fecha_vencimiento, parametro)
DETALLE_CP_TEMP(fecha, ruta, hora_inicio, secuencia, checkpoint, fecha_hora, error, fecha_hora_real, estado)
```

Se usa el valor `'???'` como registro comodín («desconocido») en varias tablas.
Los tipos exactos de cada columna no están verificados: no se ha abierto `RONDA.IBD`, que necesita Firebird.

## 4. Licenciamiento (no hace falta replicarlo)
- Licencia de instalación (`RondaLic.txt`, `FormInstalacion`) [C].
- **Licencia por captivador** (`FormLicenciaCaptivador`, tablas `CAPTOR`/`SET_CAPTOR`): fecha de vencimiento,
  días restantes y estado `ACTIVO`/`INACTIVO`. Avisa cuando quedan ≤ 10 días. Con la licencia vencida no
  deja descargar y pide un «código de habilitación» a Qwantec [C].
- Es el mecanismo comercial de Qwantec; tu app no lo necesita.

## 5. Limitaciones detectadas (mejoras para la app nueva)
1. **Memoria llena sin aviso.** Ronda no avisa cuando el bastón se acerca a 60.000 lecturas. El tuyo
   llevaba lleno desde marzo de 2023, perdiendo rondas.
   → Avisar en cada descarga (porcentaje de ocupación) y borrar siempre tras descargar con éxito.
2. **Lecturas sueltas se pierden.** Un checkpoint sin guardia a continuación, o un tag desconocido,
   se descarta en silencio [I]. → Guardar todas las lecturas crudas y emparejar después; mostrar las huérfanas.
3. **Sin duplicados controlados.** Si se descarga sin borrar, se repiten lecturas. → Clave única
   (tag, fecha-hora, número de bastón).
4. **Contraseñas en claro.** → Usar hash.
5. **Un solo PC con Firebird 1.5 local.** → Base de datos moderna (SQLite o SQL Server) y, si hace falta,
   acceso multiusuario o web.
6. **Datos personales del guardia muy chilenos** (RUT, comuna, provincia, región) y campos poco útiles
   (edad junto a fecha de nacimiento). → Revisar qué hace falta realmente.
7. **Cumplimiento calculado solo al consultar el informe.** → Calcularlo tras cada descarga y alertar de
   rutas incumplidas.

## 6. Pendiente de confirmar
- La regla exacta de emparejamiento checkpoint → guardia (ventana de tiempo, lecturas repetidas). Los estados
  2 y 3 de esa lógica son una tabla de saltos que el descompilador no reconstruyó.
- Cómo se calcula el «estado» en cumplimiento de rutas (a tiempo, fuera de margen, no realizado).
- El contenido actual de `RONDA.IBD` (guardias, checkpoints y rutas ya dados de alta), que convendría migrar.
