# Protocolo del bastón de rondas (Captor4)

Obtenido por ingeniería inversa de `Ronda.exe` (Qwantec, 2017) y del componente COM `WmWebUsb.dll`
(SDK chino "JWM", 2016), y verificado con un bastón real conectado en COM7.

## Cómo se comunica Ronda

| Captivador (`conf.ini`) | Vía | Componente |
|---|---|---|
| `Captor2` | serie (RS-232) | `transfer.exe` + `TpComm.dll` (no incluido en la carpeta) |
| `Captor3` | USB | `DllTermCommPrj.dll` (`Term_ReadRecord`, …) |
| **`Captor4`** (el configurado) | USB → puerto COM virtual | COM `WmWebUsb.Wwusb` (`WmWebUsb.dll`) |

`WmWebUsb.dll` no habla USB directamente: el bastón lleva un puente **Silicon Labs CP210x**
(`USB\VID_10C4&PID_EA60`) que Windows expone como puerto COM. La DLL abre ese puerto con
`CreateFile("\\.\COMn")`.

Ronda llama al COM así (decompilado de `Ronda.exe`, funciones `0x48c86c` y `0x490808`):

1. `OpenUsb(2003)` → la DLL lo trata como modelo interno `0x7D2`.
2. `GetTermno()` → número del captivador (se valida contra la licencia en la tabla `CAPTOR`).
3. `SetModel(2, 1)` + `SetDateTime(año, mes, día, hora, min, seg)`.
4. `SetModel(2, 1)` + `GetRecords()` → texto con una línea por lectura: `TAGID,20YYMMDDhhmmss`.
5. `SetModel(2, 1)` + `ErasureRecords()`.
6. `CloseUsb()`.

## Capa física

- Puerto COM del CP210x, **57600 baudios, 8 bits, sin paridad, 1 bit de parada**, sin control de flujo.
- **Despertar**: antes de nada se envían **20480 bytes `0xFE`** (`0x5000`) y se vacía el buffer de entrada.

## Formato de trama

```
D5 D5 | LEN_lo LEN_hi | CMD | DATA… | SUM_lo SUM_hi | 0D 0A
```

- `LEN` = número de bytes de `CMD + DATA` (little-endian).
- `SUM` = suma de 16 bits de `CMD + DATA` (little-endian).
- La respuesta repite el formato con `CMD | 0x80`.
- **Ojo:** `LEN` no siempre es correcto. La respuesta a `39` es `D5 D5 01 00 B9 01 BA 00 0D 0A`:
  declara 1 byte pero trae 2. Hay que delimitar la trama por el `0D 0A` final y calcular la suma
  sobre lo realmente recibido, como hace WmWebUsb.

Ejemplo: identificación `D5 D5 01 00 0A 0A 00 0D 0A` → respuesta `D5 D5 01 00 8A 8A 00 0D 0A`.

## Comandos

| Cmd | Respuesta | Uso en WmWebUsb | Datos enviados | Datos recibidos |
|---|---|---|---|---|
| `0A` | `8A` | detección en `OpenUsb` | — | — |
| `02` | `82` | `GetTermno` | — | 4 bytes big-endian = número (Ronda: 6 dígitos) |
| `01` | `81` | `SetTermno` | 4 bytes big-endian | — |
| `39` | `B9` | `SetModel(2, v)` | 1 byte (`v`, Ronda usa 1) | 1 byte |
| `05` | `85` | `SetDateTime` | `ss mm hh DD 00 MM YY` en BCD | — |
| `03` | `83` | `GetRecords` (cabecera) | — | 2 bytes LE = número de bloques |
| `00` | — | ACK: «envía el siguiente bloque» | — | — |
| `FF` | — | NAK: «repite el bloque» | — | — |
| `93` | — | bloque de registros (lo envía el bastón) | — | ver abajo |
| `04` | `84` | `ErasureRecords` | — | — (WmWebUsb espera 4 s antes y hasta 40 s la respuesta) |

**Importante (verificado):** el bastón **no responde al `03` si antes no se envía `39 01`**
(`SetModel(2,1)`). Ronda lo envía siempre antes de leer, borrar o poner la hora.

## Descarga de registros

```
PC → 39 01            bastón → B9 xx
PC → 03               bastón → 83 [nBloques_lo nBloques_hi]
repetir nBloques veces:
  PC → 00 (ACK)       bastón → 93 [idx_lo idx_hi] [registros…]
  (si el bloque llega mal: PC → FF (NAK), hasta 3 veces)
PC → 00 (ACK final)
```

Cada bloque `93` (66 bytes en el bastón probado) contiene, tras los 7 bytes de cabecera
(`D5 D5 LEN LEN 93 idx idx`), **5 registros de 11 bytes**:

```
T0 T1 T2 T3 T4 | ss mm hh DD MM YY
```

- `T0..T4`: identificador del botón/tag, que se muestra como 10 caracteres hex (p. ej. `3C007BC71D`).
- Fecha y hora en **BCD**, año `20YY`.
- Un registro con `FF` en todos sus primeros bytes está vacío.

La DLL también soporta una variante de **10 bytes** (tag de 4 bytes big-endian mostrado como
decimal de 10 dígitos). La elige cuando `(longitud_del_bloque − 11)` no es múltiplo de 11.
La librería C# replica esa regla.

## Resultado con el bastón real (nº 007406, 26/10/2026)

- 12.000 bloques × 5 = **60.000 registros**, todos válidos, sin reintentos. La lectura completa tarda unos 4 minutos.
- La memoria está **llena y funciona como un buffer circular**. Los registros salen en orden físico de memoria:
  el más reciente (09/03/2023 04:33:30) está en la posición 59.579, y a continuación viene el más antiguo
  (01/02/2020). El bastón devuelve **toda la memoria**, no solo las lecturas nuevas.
- Para la app nueva hay que **ordenar por fecha y descartar duplicados** de descargas anteriores (por tag + fecha-hora),
  o borrar la memoria (`04`) tras cada descarga correcta, como hace Ronda.
- No hay lecturas posteriores al 09/03/2023: o el bastón no se usa desde entonces, o su reloj está desajustado.

- Con la memoria llena, el bastón **no guarda lecturas nuevas**: al pasar por un botón enciende la luz y
  vibra **5 veces**, en lugar de la vibración única que el manual indica como lectura correcta.
- `ErasureRecords` (`04`) probado el 26/10/2026: tras el borrado, la descarga devuelve 0 registros.
- `SetDateTime` (`05`) probado el 26/10/2026: se envió a las 10:41 y una lectura posterior quedó registrada
  a las 10:55:31, con el bastón descargado a las 10:56. El reloj queda bien.

## Despertar y reposo (medido el 26/10/2026 con el bastón 007406)

- Dormido, el bastón **no responde** a la identificación (`0A`): solo devuelve un byte `00`.
- **Despertar mínimo:** 6144 bytes `0xFE` (≈1,07 s a 57600). Con 4096 o menos no despierta. WmWebUsb usa 20480
  (3,6 s); **8192 (1,4 s)** deja margen.
- **Despierto, responde en ~60 ms** sin necesidad de volver a despertarlo.
- **Vuelve a dormirse entre 5 y 15 s sin tráfico.** Una identificación cada ≤ 4 s lo mantiene despierto.

Consecuencia para la base: mientras el bastón está en ella, una identificación cada 3 s (60 ms) lo mantiene despierto
y detecta su retiro en ≤ 3 s. Cuando no está, un despertar de 8192 bytes + identificación cada ~5 s detecta su vuelta
en ≤ ~7 s, sin sensor de presencia.

## Lo que no está verificado

- `SetTermno` se ha sacado del código, pero no se ha probado para no cambiar el número del bastón.
- Qué hace exactamente el parámetro 2 de `SetModel`. Solo se sabe que Ronda lo envía siempre con valor 1
  y que sin él no funciona la descarga.
- El significado del byte `00` entre el día y el mes en `SetDateTime` (posiblemente el día de la semana);
  WmWebUsb envía siempre 0.
