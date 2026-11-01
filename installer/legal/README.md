# Textos legales de los instaladores

| Archivo | Uso |
|---|---|
| `EULA.txt` | Contrato de licencia de usuario final. Los tres instaladores (suite, cliente y complemento) lo muestran en la página «Acuerdo de licencia» y lo copian a `{app}\legal`. |
| `AVISOS-DE-TERCEROS.txt` | Avisos de copyright y licencias de los componentes de terceros, con la oferta de código fuente de los componentes LGPL/GPL. |
| `terceros\` | Textos originales: licencias completas (LGPL-3.0, GPL-3.0, Apache-2.0), avisos de .NET, SkiaSharp y WebView2, MediaMTX, PostgreSQL y las listas de código abierto que traen los SDK de Hikvision y Dahua. |

Los tres archivos viajan a `{app}\legal` en cada instalador, y a `legal/` en el
paquete de las pantallas de alarma.

## Antes de la primera distribución comercial

- **La EULA es un borrador.** Debe revisarla un abogado antes de entregarla a
  clientes, en especial las cláusulas de garantía (9), responsabilidad (10),
  datos personales (7) y jurisdicción (14).
- Completar los campos `[POR COMPLETAR: …]` de `EULA.txt` y
  `AVISOS-DE-TERCEROS.txt` (razón social, RUT, domicilio, fecha y correo de
  contacto). `installer\build-installers.ps1` avisa mientras queden campos sin
  completar.
- Los SDK de Hikvision y Dahua requieren autorización escrita del fabricante
  para redistribuirse (Hikvision TPP, programa de partners de Dahua).

## Mantenimiento

Actualizar `AVISOS-DE-TERCEROS.txt` cuando:

- se agregue, quite o cambie de versión mayor un paquete NuGet que viaje en un
  instalador. Para listarlos:
  `dotnet list CLRTrueCentralVMS.slnx package --include-transitive`;
- cambie una herramienta de `tools\` (FFmpeg, MediaMTX, PostgreSQL) o una
  librería de `wwwroot\vendor`;
- se agregue o cambie un SDK de fabricante (copiar su lista de código abierto a
  `terceros\`).

Los avisos de .NET (`terceros\dotnet-runtime-…`, `aspnetcore-…`,
`windowsdesktop-…`) salen de los paquetes de runtime
(`~\.nuget\packages\microsoft.*.app.runtime.win-x64\<versión>\`); conviene
refrescarlos al cambiar de versión mayor de .NET.
