# Capturas pendientes

Las 39 capturas que faltan en el manual. Cada una está en su capítulo como
`#captura-pendiente("…")`. Al tomarla, se reemplaza por
`#captura("archivo.png", marcas: (…), pie: […])` y se borra de esta lista.

Reglas:

- Solo datos de un entorno de demostración: nombres de personas, patentes,
  direcciones y sitios ficticios. Nunca datos de clientes.
- Panel web: Edge sin ventana a 1280×800 con escala 2, recortado a la zona que
  interesa. Cliente de monitoreo: ventana a 1600×900.
- Las marcas son la lista numerada de cada fila, en ese orden.

## 2 · Primeros pasos

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-inicio-sesion.png` | Ventana de ingreso del cliente | Servidor · Usuario con ▾ · Contraseña con su ojo · Recordar contraseña / Inicio de sesión automático · Ingresar |
| `cliente-ventana-principal.png` | Viñetas Inicio y Vista en Vivo, aviso de licencia en la barra de estado | Viñetas · CPU/RAM/disco · punto y «Conectado» · botón del usuario · riel · barra de estado |
| `cliente-inicio.png` | Página Inicio con las tarjetas (puede ser la misma imagen anterior) | Viñetas · una tarjeta · riel |
| `cliente-configuracion-video.png` | Ventana Configuración, apartado Video | Menú de apartados · Guardar |

## 3 · Vista en vivo

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-vista-en-vivo.png` | Grilla 2×2 con 3 cámaras, una seleccionada, lista por equipo, PTZ abierto | Equipo/Ubicación · Buscar canal… · División · Zoom · Limpiar todo · Vistas · Pantalla auxiliar · Pantalla completa · cuadro seleccionado · panel PTZ · barra de estado |
| `cliente-vista-en-vivo-cuadro.png` | Barra de un cuadro grabando | Número · nombre · estado · parlante · P/S · cámara · ● con contador · ✕ |
| `cliente-vista-en-vivo-ptz.png` | Panel PTZ de una cámara PTZ, con Shift apretado si se puede | Encabezado · flechas · Zoom/Foco/Iris · Preset con Ir/Guardar/Borrar · Velocidad · píldora PRECISIÓN |
| `cliente-vista-en-vivo-vistas.png` | Desplegable Vistas con una vista propia y una compartida | Fila · lápiz · basurero · Nombre de la vista… · Compartir con todos los puestos · Guardar |

## 4 · Reproducción

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-reproduccion.png` | 4 posiciones, 2–3 cámaras a la misma hora, tramos de varios colores | Árbol GRABACIONES · barra Día · 1 canal / 4 posiciones y contador · leyenda · cuadro seleccionado y su barra · transporte y velocidad · reloj · Zoom/Recorte/pantalla completa · línea de tiempo con − / + |
| `cliente-reproduccion-calendario.png` | Calendario abierto con días marcados | Flechas de mes · día con grabación · hoy · día seleccionado |
| `cliente-reproduccion-recorte.png` | Recorte activo con un tramo marcado | Recorte · tramo sombreado · «Tramo:» con ✕ · Reproducir tramo · Exportar… |
| `cliente-exportar-grabaciones.png` | Tramo de 1 h, MP4, división cada 30 min | Cámara · Inicio/Fin · Carpeta de destino con … · Formato · Archivos · resumen · Exportar |
| `cliente-centro-de-descargas.png` | Filas Descargando, Por comenzar, Terminada y Fallida | Resumen · Limpiar terminadas · estado · botones de la fila |

## 5 · Centro de eventos

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-centro-de-eventos.png` | Una alerta PENDIENTE y dos Confirmadas | Resumen · Solo sin confirmar · Ventana aparte · PENDIENTE · Ver · Enterado · «Acuse de recibo» · botón del riel con su número |
| `cliente-ventana-de-alarma.png` | Información de la alarma con consignas, 2–3 fotos, pestaña Fotos | CONSIGNAS · ORIGEN · ACUSE DE RECIBO · pestañas · pantalla completa · Enterado · Silenciar · paginador · Cerrar |

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

## 15 · Recursos y ubicaciones

| Archivo | Pantalla y estado | Marcas |
|---|---|---|
| `cliente-verificacion-aviso.png` | Verificación del aviso con dos cámaras | Consignas · ubicación · cuadro 1 · Abrir en la Vista en vivo · paginador |
