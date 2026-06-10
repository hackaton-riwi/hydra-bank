# hydra-bank

Academic project repository for Manhattan Trust Bank (ID: 194).

## API para crear tenants

El proyecto tiene autenticación con JWT e Identity. El primer usuario que se registra queda con rol `ADMIN`; los siguientes quedan como `CLIENT`.

Solo un usuario con rol `ADMIN` puede crear tenants.

### 1. Registrar el primer admin

```http
POST /api/auth/register
Content-Type: application/json

{
  "email": "admin@hydra.test",
  "password": "Admin123"
}
```

La respuesta incluye un `token`. Ese token se usa como Bearer token.

En Swagger, abre el botón `Authorize` y pega solo el valor del token, sin escribir `Bearer`. En Postman o curl sí debes enviar el header completo: `Authorization: Bearer TU_TOKEN`.

### 2. Crear tenant

```http
POST /api/tenants
Authorization: Bearer TU_TOKEN_ADMIN
Content-Type: application/json

{
  "name": "Manhattan Trust Bank",
  "slug": "manhattan-trust",
  "mainCurrency": "USD",
  "maxTransactionAmount": 5000000,
  "feeType": "FIXED",
  "feeValue": 2.5,
  "webhookUrl": "https://example.com/webhooks/hydra"
}
```

`feeType` acepta `FIXED` o `PERCENTAGE`. Si usas `PERCENTAGE`, `feeValue` no puede ser mayor a `100`.

El `slug` debe ser único y solo puede tener letras minúsculas, números y guiones, por ejemplo `mi-banco`.

### Respuestas esperadas

- `201 Created`: tenant creado.
- `400 Bad Request`: datos inválidos.
- `401 Unauthorized`: no enviaste token válido.
- `403 Forbidden`: el usuario no tiene rol `ADMIN`.
- `409 Conflict`: ya existe un tenant con ese `slug`.
