#import "../plantilla.typ": *
// Fuentes: reúne los atajos descritos en cada capítulo (LiveView.xaml.cs, PlaybackView.xaml.cs,
// TimelineBar.cs, ImageViewerWindow.xaml.cs, WallView.xaml.cs, LprView.xaml.cs, live.js, anpr.js, walls.js,
// workflows.js)

= Atajos de teclado y mouse <ap-atajos>

Este apéndice reúne los atajos de todos los módulos. Cada uno se explica con
más detalle en su capítulo.

== Inicio de sesión y ventana principal

#table(
  columns: (auto, 1fr),
  table.header[Tecla o gesto][Acción],
  [#tecla("Enter")], [En la ventana de ingreso del cliente: ingresar.],
  [Arrastrar una viñeta a lo largo de la barra], [Cambiarla de lugar.],
  [Arrastrar una viñeta fuera de la barra], [Llevar ese módulo a su propia
    ventana.],
  [Soltar la viñeta de esa ventana sobre la barra principal], [Devolver el
    módulo a la ventana principal.],
  [#tecla("Esc")], [En el panel web: cerrar el menú del usuario.],
)

#modulo("video")[
  == Centro de mando (cliente de monitoreo)

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [Doble clic en una cámara o una ubicación de la lista], [Abrir sus cámaras
      en la grilla (hasta 64).],
    [Doble clic en un recurso, una tarjeta o una página web], [Abrirlo en el
      cuadro seleccionado o en el primero libre.],
    [Arrastrar una cámara o un recurso a un cuadro], [Abrirlo en ese cuadro.],
    [Arrastrar un cuadro sobre otro], [Intercambiarlos.],
    [Doble clic en un cuadro], [Maximizarlo dentro de la grilla o devolverlo.],
    [#tecla("Ctrl") + rueda del mouse sobre una página web], [Acercar o alejar
      la página.],
    [Rueda del mouse sobre el video], [Zoom digital centrado en el puntero.],
    [Clic derecho con el modo zoom activo], [Volver a 1×.],
    [Clic derecho en una ubicación], [Menú de órdenes de la ubicación.],
    [#tecla("↑") #tecla("↓") #tecla("←") #tecla("→")], [Mover la cámara PTZ del
      cuadro seleccionado mientras se mantiene la tecla.],
    [#tecla("+") #tecla("−")], [Acercar o alejar la cámara PTZ (también las del
      teclado numérico).],
    [#tecla("Shift") sostenido], [Modo precisión: la cámara PTZ se mueve a la
      velocidad mínima.],
    [Clic en un cuadro], [Seleccionarlo.],
    [#tecla("Esc")], [Salir de la pantalla completa, cerrar el aviso de
      apertura masiva o, si no, dejar la grilla sin cuadro seleccionado.],
  )

  == Vista en vivo (panel web)

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [#tecla("↑") #tecla("↓") #tecla("←") #tecla("→"), #tecla("+") #tecla("−"),
      #tecla("Shift")], [Control PTZ, igual que en el cliente.],
    [Rueda del mouse sobre el video], [Zoom digital.],
    [Arrastrar sobre el video con zoom], [Desplazar la imagen.],
    [Clic derecho sobre el video], [Volver a 1×.],
    [#tecla("Enter")], [Guardar la vista cuyo nombre se está escribiendo.],
    [#tecla("Esc")], [Cerrar los menús desplegables y salir de la pantalla
      completa del navegador.],
  )
]

#modulo("playback")[
  == Reproducción

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [Doble clic en una cámara del árbol], [Agregarla a la reproducción.],
    [#tecla("Ctrl") + doble clic en una cámara], [Dejar solo esa cámara.],
    [Arrastrar una cámara a una posición], [Ponerla en esa posición.],
    [Doble clic en el tercio izquierdo / derecho del video], [Retroceder /
      avanzar 30 segundos.],
    [Doble clic al centro del video], [Entrar o salir de la pantalla completa.],
    [#tecla("Esc")], [Salir de la pantalla completa.],
    [Clic en la línea de tiempo], [Saltar a esa hora.],
    [Arrastrar la línea de tiempo], [Mover la franja; al soltar, reproducir
      desde la hora bajo la aguja.],
    [Rueda del mouse sobre la línea de tiempo], [Acercar o alejar la vista
      (de 24 horas a 1 minuto).],
    [Clic derecho en la línea de tiempo], [Borrar el tramo marcado.],
    [Con #boton("Zoom") activo: arrastrar / rueda / clic derecho], [Marcar el
      área a acercar / acercar sobre el puntero / volver a 1×.],
  )
]

#modulo("automation")[
  == Editor de automatizaciones

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [Rueda del mouse], [Acercar o alejar, centrado en el puntero.],
    [Arrastrar el fondo], [Desplazar el diagrama.],
    [Arrastrar un paso], [Cambiarlo de lugar.],
    [Doble clic en el fondo], [Acercar.],
    [#tecla("Supr")], [Eliminar el paso o la flecha seleccionada.],
    [#tecla("Esc")], [Quitar la selección y cerrar el menú _Agregar después_.],
  )

  == Visor de fotos de las alertas

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [#tecla("←") #tecla("→")], [Foto anterior o siguiente.],
    [#tecla("+") #tecla("−") o rueda del mouse], [Acercar o alejar.],
    [#tecla("0")], [Ajustar a la pantalla.],
    [#tecla("1")], [Tamaño real.],
    [Arrastrar], [Mover la imagen ampliada.],
    [Doble clic], [Alternar entre ajustada y tamaño real.],
    [#tecla("Esc")], [Cerrar el visor.],
  )
]

#modulo("videowall")[
  == Muro de video

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [Arrastrar una cámara a una ventana], [Asignarla.],
    [Arrastrar una ventana sobre otra], [Intercambiarlas.],
    [Clic / #tecla("Ctrl") + clic], [Seleccionar una ventana / varias.],
    [Doble clic en una ventana], [Pantalla completa en su monitor.],
    [#tecla("Ctrl") + doble clic], [Pantalla completa en todo el muro.],
    [Doble clic en una ventana flotante], [Cubrir los monitores que toca.],
    [Clic derecho en una ventana], [Menú de la ventana.],
  )
]

#modulo("anpr")[
  == Reconocimiento de patentes

  #table(
    columns: (auto, 1fr),
    table.header[Tecla][Acción],
    [#tecla("Enter")], [Buscar (en el cliente y en el panel web).],
    [#tecla("↑") #tecla("↓")], [Recorrer la lista de lecturas.],
  )
]

#modulo("speakers")[
  == Parlantes IP

  #table(
    columns: (auto, 1fr),
    table.header[Tecla o gesto][Acción],
    [Mantener presionado #boton("Mantener para hablar")], [Hablar por los
      parlantes marcados; al soltar (o al sacar el puntero del botón) se corta
      la voz.],
    [#tecla("Esc")], [En el panel web: cerrar el control de volumen.],
  )
]
