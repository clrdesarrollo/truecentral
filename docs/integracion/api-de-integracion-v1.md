# API de integración de TrueCentral — v1

La API de integración permite que un sistema externo lea el padrón de personas de TrueCentral. Por ejemplo:

- el kiosco de un casino;
- un sistema de remuneraciones o de asistencia.

El sistema externo no usa un usuario del VMS. Usa una **aplicación integrada**, que se crea en el panel en *Sistema → Integraciones* y tiene:

- una **llave** (AppKey);
- un **secreto** (AppSecret), que se muestra una sola vez;
- **permisos**;
- **direcciones permitidas** (opcional);
- una **fecha de vencimiento** (opcional).

Cada aplicación habilitada consume una unidad del cupo `integration_apps` de la licencia (módulo `module_integration_api`).

La v1 es **solo de lectura**. Lo nuevo se agrega como campos opcionales o en una v2, sin romper a quien ya integra.

## Conexión

- Siempre por **HTTPS**, al puerto web del servidor (443 por defecto).
- El servidor usa un certificado propio. En vez de la cadena de confianza, verifique la **huella de su llave pública** (SPKI): SHA-256 en hexadecimal del `SubjectPublicKeyInfo` del certificado.
  - El panel la muestra al crear la aplicación.
  - En .NET: `Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()))`.
- El reloj del sistema externo debe estar en hora: se aceptan **±5 minutos** de diferencia.

## Firma

Cada petición lleva cuatro cabeceras:

| Cabecera | Valor |
|---|---|
| `X-TC-AppKey` | La llave de la aplicación. |
| `X-TC-Timestamp` | La hora actual en segundos Unix (UTC). |
| `X-TC-Nonce` | Un valor nuevo en cada petición, de 8 a 64 caracteres (por ejemplo un GUID sin guiones). |
| `X-TC-Signature` | `Base64(HMAC-SHA256(secreto, texto))`. |

El `texto` son cinco líneas unidas con `\n`, sin salto de línea al final:

```
MÉTODO                  ← en mayúsculas: GET
RUTA_Y_CONSULTA         ← tal como viaja: /api/integration/v1/persons?page=1&pageSize=200
TIMESTAMP               ← el mismo de X-TC-Timestamp
NONCE                   ← el mismo de X-TC-Nonce
SHA256_DEL_CUERPO       ← hexadecimal en minúsculas; sin cuerpo, el de la cadena vacía:
                          e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
```

La firma cubre el método, la ruta, la consulta y el cuerpo. Por eso:

- una petición capturada no se puede modificar;
- tampoco se puede repetir, porque el servidor acepta cada nonce una sola vez.

Ejemplo en Python:

```python
import base64, hashlib, hmac, time, uuid

def headers(app_key, secret, path_and_query, method="GET", body=b""):
    ts = str(int(time.time()))
    nonce = uuid.uuid4().hex
    text = "\n".join([method, path_and_query, ts, nonce, hashlib.sha256(body).hexdigest()])
    signature = base64.b64encode(hmac.new(secret.encode(), text.encode(), hashlib.sha256).digest()).decode()
    return {"X-TC-AppKey": app_key, "X-TC-Timestamp": ts, "X-TC-Nonce": nonce, "X-TC-Signature": signature}
```

## Rutas

| Método | Ruta | Permiso | Respuesta |
|---|---|---|---|
| GET | `/api/integration/v1/health` | — | `{ ok, version, time, app, scopes }`. Sirve para probar la conexión. |
| GET | `/api/integration/v1/persons?page=&pageSize=&updatedSince=` | `persons.read` | `{ total, page, pageSize, items: [persona] }`, en orden fijo. `pageSize` va de 1 a 500 (200 por defecto). |
| GET | `/api/integration/v1/persons/{employeeNo}/photo` | `persons.photo` | La foto del rostro (`image/jpeg` o `image/png`). Responde 404 si la persona no existe o no tiene foto. |
| GET | `/api/integration/v1/departments` | `persons.read` | `[{ name, path, persons }]` |

Cada persona trae:

```json
{
  "employeeNo": "19456340",
  "id": 57,
  "firstName": "Ana",
  "lastName": "Pérez",
  "fullName": "Ana Pérez",
  "department": "Casino",
  "departmentPath": "Planta / Casino",
  "position": "Supervisora",
  "enabled": true,
  "validFrom": "2026-01-01T03:00:00Z",
  "validTo": "2099-12-31T02:59:59Z",
  "attributes": { "Colacion": "1" },
  "photo": { "version": "12-8df36b0e606347e-48211", "contentType": "image/jpeg", "bytes": 48211, "updatedAt": "2026-10-30T14:43:24Z" },
  "updatedAt": "2026-10-30T14:43:24Z"
}
```

- **`employeeNo`** es el legajo con que reconocen a la persona los terminales de acceso, y no cambia nunca. Es la llave para cruzar.
- **`department`** es el último tramo de la ruta del departamento; `departmentPath` es la ruta completa.
- **`attributes`** trae los campos personalizados, por nombre, con el valor normalizado según su tipo:
  - número con punto decimal;
  - sí/no como `true` o `false`;
  - fecha como `aaaa-mm-dd`.

  Un campo sin valor no aparece.
- **`photo`** es `null` si la persona no tiene foto. `version` cambia cuando se reemplaza la foto, así que una foto con la misma versión no necesita volver a descargarse.
- **`updatedSince`** trae solo lo modificado desde esa fecha. Una persona **eliminada** no aparece ahí: para detectar las bajas hay que recorrer el padrón completo y comparar.

## Errores

Los errores responden `{ "error": "texto en español", "code": "…" }`:

| HTTP | `code` | Motivo |
|---|---|---|
| 401 | `headers` | Faltan las cabeceras de la firma. |
| 401 | `unknown-key` | La llave no existe. |
| 401 | `clock` | La hora difiere en más de 5 minutos. |
| 401 | `nonce` | El nonce ya se usó, o su largo no es válido. |
| 401 | `signature` | La firma no coincide. |
| 402 | `license` | La licencia no incluye el módulo o no alcanza para la aplicación. |
| 403 | `disabled` | La aplicación está deshabilitada. |
| 403 | `expired` | La aplicación venció. |
| 403 | `ip` | La dirección de origen no está permitida. |
| 403 | `scope` | La aplicación no tiene el permiso de esa ruta. |

Todos los rechazos quedan en la bitácora de TrueCentral, en la categoría «API de integración», con el motivo y la dirección de origen.
