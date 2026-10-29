# Capturas pendientes

Las 37 capturas que faltan en el manual. Cada una está en su capítulo como
`#captura-pendiente("…")`. Al tomarla, se reemplaza por
`#captura("archivo.png", marcas: (…), pie: […])` y se borra de esta lista.

Reglas:

- Solo datos de un entorno de demostración: nombres de personas, patentes,
  direcciones y sitios ficticios. Nunca datos de clientes.
- Panel web: Edge sin ventana a 1280×800 con escala 2, recortado a la zona que
  interesa. Cliente de monitoreo: ventana a 1600×900.
- Las marcas son la lista numerada de cada fila, en ese orden.

## 3 · Centro de mando

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-centro-de-mando.png` | Tablero cargado con cámaras, una puerta, un panel de alarma, la tarjeta Estado de los equipos y una página web; la lista RECURSOS abierta en una ubicación con cámaras y recursos (reemplaza a `cliente-vista-en-vivo.png`) | Lista RECURSOS · Buscar… · Tarjetas / Grupos de parlantes / Páginas web · Tableros y su nombre · cuadro de puerta · cuadro de alarma · tarjeta · página web con Recargar y zoom |
| `cliente-centro-de-mando-tableros.png` | Desplegable TABLEROS con un tablero propio y uno compartido con candado (reemplaza a `cliente-vista-en-vivo-vistas.png`) | Candado · detalle «N cámara(s) y M cuadro(s) más · división · para …» · lápiz · basurero · Nombre del tablero… y Guardar · Compartir con todos |
| `web-tableros-editor.png` | Configuración › Tableros, Nuevo tablero compartido con división 4, «Solo estos roles:» con un rol, dos cuadros llenos y el cuadro 3 elegido | Nombre · División · Lo ven · grilla con un cuadro lleno y su × · Buscar cámara, puerta, tarjeta, página… · Otra dirección con Rótulo y Poner · Guardar |
| `cliente-vista-en-vivo-ptz.png` | Panel PTZ de una cámara PTZ, con Shift apretado si se puede | Encabezado · flechas · Zoom/Foco/Iris · Preset con Ir/Guardar/Borrar · Velocidad · píldora PRECISIÓN |

## 4 · Reproducción

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-reproduccion.png` | 4 posiciones, 2–3 cámaras a la misma hora, tramos de varios colores | Árbol GRABACIONES · barra Día · 1 canal / 4 posiciones y contador · leyenda · cuadro seleccionado y su barra · transporte y velocidad · reloj · Zoom/Recorte/pantalla completa · línea de tiempo con − / + |
| `cliente-reproduccion-calendario.png` | Calendario abierto con días marcados | Flechas de mes · día con grabación · hoy · día seleccionado |
| `cliente-reproduccion-recorte.png` | Recorte activo con un tramo marcado | Recorte · tramo sombreado · «Tramo:» con ✕ · Reproducir tramo · Exportar… |

## 6 · Paneles de alarma

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-paneles-de-alarma.png` | Dos paneles; el elegido con un área armada, otra «Armando… (N s)», una zona anulada y otra activada | Resumen · lista de paneles · Actualizar · botones del panel · tarjeta de área · Anular/Restituir · filtros · Eventos |
| `web-alarmas.png` | Filtros, un panel elegido y sus eventos | Tipo de evento · Buscar texto · Desde/Hasta · Solo el panel seleccionado · Buscar/Limpiar · columnas |
| `web-paneles-de-alarma-directo.png` | Agregar panel, conexión directa, «Conexión validada» | Marca / protocolo · dirección y puerto · usuario y contraseña · HTTPS · Monitoreo activo · Probar conexión · Guardar |

## 7 · Cerco eléctrico

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-cerco-electrico.png` | Tres tarjetas: en alarma, armada con señal y sin conexión | Conexión · etiquetas · Armar/Desarmar/Silenciar · Eventos recientes |
| `web-cerco-aviso.png` | Aviso «Alarma de cerco eléctrico» fijo arriba | Silenciar sirena · Ver monitor · Reconocer · indicación de activar el sonido |
| `web-paneles-de-cerco-firmware.png` | Configurar › Firmware con barra de progreso | Versión instalada · archivo · Actualizar firmware · estado |

## 8 · Control de acceso

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `web-acceso-monitoreo.png` | Puertas normal, mantenida abierta, bloqueada y sin conexión | Casilla y Seleccionar todas · órdenes por lote · filtro y buscador · resumen · una tarjeta · Lo que va pasando · Último acceso |
| `web-acceso-registros.png` | Últimos 7 días, dos puertas marcadas | Período · Puntos de acceso · Resultado y Credencial · Buscar por · Buscar/Limpiar · reporte Excel/PDF · columnas ordenables · paginación |
| `web-acceso-equipo.png` | Página de un equipo, pestaña Conexión, una puerta pausada | Encabezado · Revalidar · pestañas · Probar conexión / Guardar cambios · Puertas del equipo |
| `web-acceso-equipo-partes.png` | Terminal facial, pestaña Conexión, sección Partes del terminal con las tres casillas marcadas; cámara y altavoz en uso, 1 entrada y 1 salida | Título «(incluidas: no ocupan licencia)» · Su cámara y su estado · Su altavoz y su estado · Sus entradas y salidas y su estado |

## 9 · Citofonía

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-citofonia.png` | Un frente en línea y varias llamadas en el historial | FRENTES DE CITOFONÍA · Ver y hablar · Abrir puerta · ÚLTIMAS LLAMADAS · Actualizar · botón del riel |
| `cliente-citofonia-llamada.png` | Ventana de llamada en LLAMADA ENTRANTE | Estado · contador · video · Contestar · Rechazar · Silenciar timbre en este puesto · Abrir puerta |
| `web-citofonia-frentes.png` | Agregar frente de citofonía con «Conexión validada» | Cámara del frente · casillas · Probar conexión · resultado |

## 10 · Parlantes IP

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-vista-en-vivo-parlantes.png` | Panel PARLANTES con dos parlantes marcados | Marcar todos · casillas · Volumen · micrófono, Probar y nivel · Mantener para hablar · Detener · tono · sonido del servidor · biblioteca · texto a voz · estado |
| `web-parlantes.png` | Tabla y formulario tras Probar conexión | Reproducir en varios… · Agregar parlante · Volumen · Biblioteca · resultado |
| `cliente-centro-de-mando-grupo-parlantes.png` | Centro de mando con el cuadro del grupo «Perímetro», un integrante sin conexión | Nombre y resumen · Hablar al grupo · Detener · sonido y Reproducir · línea de estado · PARLANTES con el punto de cada integrante |

## Módulos de E/S

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `web-modulos-es.png` | Dispositivos › Módulos de E/S con un módulo en línea, una entrada activa y un relé encendido | Nombre y ubicación · estado de conexión · entradas · relés · segundos del pulso · Pulso / Encender / Apagar |

## Pantallas de alarma

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `pi-pantalla-alarma.png` | La app de la pantalla (Raspberry Pi) con avisos en la lista, uno pendiente seleccionado con foto y consignas | Nombre de la pantalla · conexión · SIN VER · QUÉ HACER · MARCAR COMO VISTA · TODAS VISTAS |

## 11 · Muro de video

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-muro-de-video.png` | Muro 2×2 con cámaras asignadas | Muro: · herramientas · ventanas del monitor · ventana con ✕ · Cámaras y Buscar canal… · pestaña « » · barra de estado |
| `cliente-muro-proyectar.png` | Proyectar al muro de video | Destino · Una pantalla de este PC · Un archivo de video y Examinar… · Reproducir en bucle · IP de este PC · Iniciar |
| `web-muro-nuevo.png` | Nuevo muro de video 2×2 | Decodificador · Filas/Columnas · Leer salidas del decodificador · Salida y Ventanas · Guardar muro |

## 12 · Reconocimiento de patentes

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-patentes.png` | Lista y ficha (patentes ficticias) | Fuentes activas · filtro de equipo · patente · Buscar/Limpiar · Seguir en vivo · Fuentes · lista · Copiar patente / Guardar imágenes · escena · datos |
| `cliente-patentes-fuentes.png` | Panel Fuentes desplegado | Casilla · dirección y lecturas · estado · error en rojo |
| `web-anpr.png` | Filtros, lista y ficha | Fuentes junto al título · filtros · Mostrar · contador y Seguir en vivo · lista · botones de la ficha |
| `web-fuentes-de-video-patentes.png` | Fuentes de video, columna Patentes | Un «Activo» · un «Activar» · un «—» |

## 14 · Fuentes de video

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `web-fuentes-de-video-cambiar-ip.png` | Cambiar IP | Modo · IP nueva «está libre» · Sincronizar fecha y hora · Aplicar |
| `web-hora-y-mantenimiento.png` | Terminales de acceso, uno Desfasado | Tarjetas · Política… · Poner en hora · Estado · Corrección automática · Ajustar… / Reiniciar / Restablecer… |
| `web-fuentes-de-video-partes.png` | Editar dispositivo de una cámara Hikvision con altavoz y entradas y salidas de alarma | Partes del equipo · Puerto HTTP (ISAPI) · Su altavoz, como un parlante más y su texto · Sus entradas y salidas de alarma… y su texto |

## 15 · Recursos y ubicaciones

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-verificacion-aviso.png` | Verificación del aviso con dos cámaras | Consignas · ubicación · cuadro 1 · Abrir en el Centro de mando · paginador |
| `web-recursos-ficha-parametros.png` | Ficha de una puerta de un terminal Hikvision, pestaña Parámetros, un bloque con cambios sin guardar | Pestaña Parámetros · enlace al equipo · bloque de la puerta · Lector 1 · lector de entrada · «Hay cambios sin guardar.» y Guardar |
