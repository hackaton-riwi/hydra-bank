# Hydra API — Documentación de Endpoints

> Base URL: `https://<host>/api/v1`  
> Autenticación: `Authorization: Bearer <token>` (JWT)  
> Rate limiting aplicado en todos los endpoints.

---

## 🔐 Auth

**Base:** `/api/v1/auth`  
Acceso público excepto `/me`.

---

### `POST /auth/register`

Registra un nuevo cliente en un tenant. Crea el usuario, cuenta bancaria y audit log en una sola transacción atómica.

> **Auth:** ❌ No requerida

**Body:**
```json
{
  "email": "string",
  "password": "string",
  "fullName": "string",
  "documentNumber": "string",
  "tenantSlug": "string"
}
```

**Respuesta exitosa:** `201 Created`
```json
{
  "success": true,
  "code": "CLIENT_REGISTERED",
  "description": "Cliente registrado correctamente. Debe iniciar sesión para obtener token.",
  "user": {
    "id": "USR-XXXXXXXX",
    "tenantId": "TEN-XXXXXXXX",
    "fullName": "string",
    "documentNumber": "string",
    "email": "string",
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "ACC-XXXXXXXX",
    "accountNumber": "string",
    "ownerId": "USR-XXXXXXXX",
    "balance": 0,
    "currency": "COP",
    "status": "ACTIVE",
    "createdAt": "datetime"
  }
}
```

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `400` | `DOCUMENT_REQUIRED` | Número de documento vacío |
| `404` | `TENANT_NOT_FOUND` | Tenant no existe |
| `409` | `EMAIL_ALREADY_EXISTS` | Email ya registrado en el tenant |
| `409` | `DOCUMENT_ALREADY_EXISTS` | Documento ya registrado en el tenant |

---

### `POST /auth/login`

Autentica un usuario y retorna un JWT con claims del tenant.

> **Auth:** ❌ No requerida

**Body:**
```json
{
  "email": "string",
  "password": "string",
  "tenantSlug": "string"
}
```

**Respuesta exitosa:** `200 OK`
```json
{
  "token": "string (JWT)",
  "expiresAt": "datetime",
  "user": {
    "id": "USR-XXXXXXXX",
    "tenantId": "TEN-XXXXXXXX",
    "fullName": "string",
    "documentNumber": "string",
    "email": "string",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": { ... }
}
```

> El JWT incluye los claims: `tenant_id`, `user_id`, `tenant_role`, `identity_user_id`.

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `401` | `INVALID_TENANT_CREDENTIALS` | Tenant, usuario o contraseña inválidos |

---

### `GET /auth/me`

Retorna el perfil del usuario autenticado y su cuenta asociada.

> **Auth:** ✅ Cualquier rol

**Respuesta exitosa:** `200 OK`
```json
{
  "user": {
    "id": "USR-XXXXXXXX",
    "tenantId": "TEN-XXXXXXXX",
    "fullName": "string",
    "documentNumber": "string",
    "email": "string",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": { ... }
}
```

---

## 🏦 Accounts

**Base:** `/api/v1/accounts`  
Todos los endpoints requieren rol `CLIENT`.

---

### `POST /accounts`

Crea una cuenta bancaria.

> **Auth:** ✅ Rol `CLIENT`

**Body:** `CreateAccountDto` (ver DTOs)

**Respuesta exitosa:** `201 Created` — objeto de la cuenta creada.

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `401` | `UNAUTHORIZED` | Sin permisos |
| `400` | `ACCOUNT_CREATE_FAILED` | Error al crear la cuenta |

---

### `DELETE /accounts/{accountKey}`

Desactiva una cuenta bancaria.

> **Auth:** ✅ Rol `CLIENT`

**Path param:** `accountKey` — ID o key de la cuenta.

**Respuesta exitosa:** `200 OK`

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `400` | `ACCOUNT_DEACTIVATE_FAILED` | No se pudo desactivar |

---

### `POST /accounts/recharge`

Recarga saldo a una cuenta.

> **Auth:** ✅ Rol `CLIENT`

**Body:**
```json
{
  "accountKey": "string",
  "amount": 0
}
```

**Respuesta exitosa:** `200 OK`

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `400` | `ACCOUNT_RECHARGE_FAILED` | Error al recargar |

---

### `GET /accounts/transactions`

Historial de transacciones del cliente autenticado.

> **Auth:** ✅ Rol `CLIENT`

**Query params:** `TransactionHistoryQueryDto` (filtros de fecha, tipo, paginación)

**Respuesta exitosa:** `200 OK` — lista de transacciones.

---

### `POST /accounts/transfer`

Transferencia entre cuentas. Soporta idempotencia.

> **Auth:** ✅ Rol `CLIENT`

**Headers opcionales:**
- `Idempotency-Key: UUID`
- `X-Correlation-ID: UUID` *(también se retorna en la respuesta)*

**Body:**
```json
{
  "sourceAccountKey": "string",
  "destinationAccountKey": "string",
  "amount": 0
}
```

**Respuesta exitosa:** `200 OK` + header `X-Correlation-ID`

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `423` | `IDEMPOTENCY_CONFLICT` | Transacción duplicada en progreso |
| `400` | `TRANSFER_FAILED` | Error en la transferencia |

---

### `POST /accounts/deposit`

Depósito a una cuenta. Idempotente.

> **Auth:** ✅ Rol `CLIENT`

**Headers opcionales:** `Idempotency-Key`, `X-Correlation-ID`

**Body:**
```json
{
  "accountKey": "string",
  "amount": 0
}
```

**Respuesta exitosa:** `200 OK` + header `X-Correlation-ID`

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `423` | `IDEMPOTENCY_CONFLICT` | Transacción duplicada |
| `400` | `DEPOSIT_FAILED` | Error en el depósito |

---

### `POST /accounts/withdraw`

Retiro de fondos de una cuenta. Idempotente.

> **Auth:** ✅ Rol `CLIENT`

**Headers opcionales:** `Idempotency-Key`, `X-Correlation-ID`

**Body:**
```json
{
  "accountKey": "string",
  "amount": 0
}
```

**Respuesta exitosa:** `200 OK` + header `X-Correlation-ID`

**Errores posibles:**

| Código HTTP | Code | Descripción |
|-------------|------|-------------|
| `423` | `IDEMPOTENCY_CONFLICT` | Transacción duplicada |
| `400` | `WITHDRAW_FAILED` | Error en el retiro |

---

## 💸 Transactions

**Base:** `/api/v1/transactions`  
Todos los endpoints requieren rol `CLIENT`.

---

### `POST /transactions/transfer`

Transferencia usando contexto del token (tenant y usuario extraídos del JWT).

> **Auth:** ✅ Rol `CLIENT`

**Headers requeridos:**
- `Idempotency-Key: UUID` *(si no se envía, se genera uno automáticamente)*
- `X-Correlation-ID: UUID` *(si no se envía, se genera uno automáticamente)*

**Body:**
```json
{
  "sourceAccountKey": "string",
  "destinationAccountKey": "string",
  "amount": 0
}
```

**Respuesta exitosa:** `200 OK` + header `X-Correlation-ID`

**Errores posibles:**

| Código HTTP | Descripción |
|-------------|-------------|
| `423` | Transacción en progreso (`TransactionInProgressException`) |
| `400` | Parámetros inválidos o error de negocio |

---

## 🏢 Tenants

**Base:** `/api/v1/tenants`

---

### `GET /tenants`

Lista todos los tenants registrados con su configuración.

> **Auth:** ❌ No requerida

**Respuesta exitosa:** `200 OK`
```json
{
  "tenants": [
    {
      "id": "TEN-XXXXXXXX",
      "name": "string",
      "slug": "string",
      "mainCurrency": "COP",
      "maxTransactionAmount": 5000000,
      "feeType": "FIXED",
      "feeValue": 0,
      "createdAt": "datetime"
    }
  ]
}
```

---

### `POST /tenants`

Crea un nuevo tenant con su usuario ADMIN. El slug se genera automáticamente desde el nombre.

> **Auth:** ❌ No requerida

**Body:**
```json
{
  "nombreTenant": "string",
  "correo": "string",
  "password": "string"
}
```

**Respuesta exitosa:** `201 Created`
```json
{
  "tenant": {
    "id": "TEN-XXXXXXXX",
    "name": "string",
    "slug": "string",
    "mainCurrency": "COP",
    "maxTransactionAmount": 5000000,
    "feeType": "FIXED",
    "feeValue": 0,
    "webhookUrl": null,
    "createdAt": "datetime",
    "updatedAt": "datetime"
  },
  "admin": {
    "id": "USR-XXXXXXXX",
    "tenantId": "TEN-XXXXXXXX",
    "fullName": "string",
    "email": "string",
    "role": "ADMIN",
    "createdAt": "datetime"
  }
}
```

---

### `GET /tenants/{tenantKey}/users`

Lista todos los usuarios del tenant con sus cuentas y balance total acumulado.

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`  
> Un ADMIN solo puede consultar su propio tenant.

**Path param:** `tenantKey` — slug, shortId (`TEN-XXXXXXXX`) o UUID del tenant.

**Respuesta exitosa:** `200 OK`
```json
{
  "tenant": { ... },
  "totalUsers": 0,
  "totalBalance": 0,
  "users": [
    {
      "id": "USR-XXXXXXXX",
      "tenantId": "TEN-XXXXXXXX",
      "fullName": "string",
      "documentNumber": "string",
      "email": "string",
      "role": "CLIENT",
      "createdAt": "datetime",
      "accounts": [ { ... } ]
    }
  ]
}
```

---

### `GET /tenants/{tenantKey}/transactions`

Historial de transacciones del tenant con filtros y paginación.

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`

**Path param:** `tenantKey`

**Query params:**

| Param | Tipo | Descripción |
|-------|------|-------------|
| `from` | `datetime` | Fecha inicio |
| `to` | `datetime` | Fecha fin |
| `type` | `TransactionType` | Tipo de transacción |
| `limit` | `int` | Registros por página (1–200, default 50) |
| `offset` | `int` | Desplazamiento (default 0) |

**Respuesta exitosa:** `200 OK`
```json
{
  "tenant": { ... },
  "limit": 50,
  "offset": 0,
  "total": 0,
  "totalMoved": 0,
  "totalFees": 0,
  "items": [
    {
      "id": "TRX-XXXXXXXX",
      "userId": "USR-XXXXXXXX",
      "userName": "string",
      "userDocument": "string",
      "type": "string",
      "originalAmount": 0,
      "feeAmount": 0,
      "sourceAccountId": "ACC-XXXXXXXX",
      "destinationAccountId": "ACC-XXXXXXXX",
      "status": "SUCCESS",
      "correlationId": "string",
      "createdAt": "datetime"
    }
  ]
}
```

---

### `GET /tenants/{tenantKey}/audit-logs`

Logs de auditoría del tenant (paginados).

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`

**Query params:** `limit` (default 50, max 200), `offset` (default 0)

**Respuesta exitosa:** `200 OK`
```json
{
  "tenant": { ... },
  "limit": 50,
  "offset": 0,
  "total": 0,
  "logs": [
    {
      "id": "LOG-XXXXXXXX",
      "userId": "USR-XXXXXXXX",
      "userName": "string",
      "action": "string",
      "oldValue": null,
      "newValue": "string (JSON)",
      "createdAt": "datetime"
    }
  ]
}
```

---

### `GET /tenants/current/users`

Alias de `/tenants/{tenantKey}/users` usando el `tenant_id` del token.

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`

---

### `GET /tenants/current/transactions`

Alias de `/tenants/{tenantKey}/transactions` usando el `tenant_id` del token.

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`

**Query params:** igual que `/tenants/{tenantKey}/transactions`

---

### `GET /tenants/current/audit-logs`

Alias de `/tenants/{tenantKey}/audit-logs` usando el `tenant_id` del token.

> **Auth:** ✅ Roles `ADMIN` / `SUPERADMIN`

**Query params:** `limit`, `offset`

---

### `DELETE /tenants/{tenantKey}`

Elimina un tenant con todos sus datos: usuarios, cuentas, transacciones, audit logs e identidades de autenticación.

> **Auth:** ✅ Solo rol `SUPERADMIN`

**Respuesta exitosa:** `200 OK`
```json
{
  "success": true,
  "code": "TENANT_DELETED",
  "description": "...",
  "tenant": { ... },
  "deleted": {
    "users": 0,
    "accounts": 0,
    "transactions": 0,
    "auditLogs": 0,
    "idempotencyRecords": 0
  }
}
```

---

## Roles

| Rol | Descripción |
|-----|-------------|
| `CLIENT` | Usuario final del tenant. Accede a cuentas y transacciones propias. |
| `ADMIN` | Administrador de un tenant. Consulta usuarios, transacciones y logs de su tenant. |
| `SUPERADMIN` | Acceso total. Puede administrar cualquier tenant y eliminarlos. |

---

## Notas generales

- Los IDs se retornan en formato short: `USR-XXXXXXXX`, `TEN-XXXXXXXX`, `ACC-XXXXXXXX`, `TRX-XXXXXXXX`, `LOG-XXXXXXXX`.
- El `tenantKey` acepta tres formatos: **slug** (`mi-banco`), **shortId** (`TEN-XXXXXXXX`) o **UUID**.
- Las operaciones financieras (`transfer`, `deposit`, `withdraw`) son idempotentes. Enviar `Idempotency-Key` como UUID en el header previene duplicados.
- El header `X-Correlation-ID` se retorna en la respuesta para trazabilidad.
- El JWT expira según configuración (`Jwt:ExpireMinutes`), por defecto **60 minutos**.