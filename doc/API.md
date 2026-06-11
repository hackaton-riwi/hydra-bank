# API Reference — BankOS v1

Todas las rutas están bajo el prefijo `/api/v1/`.

## Headers Requeridos

| Header | Operaciones Mutables | Solo Lectura |
|--------|---------------------|--------------|
| `Authorization: Bearer <token>` | ✅ | ✅ |
| `Idempotency-Key: <uuid>` | ✅ (depósito, retiro, transferencia) | ❌ |
| `X-Correlation-ID: <uuid>` | ✅ (opcional, se genera si no se envía) | ❌ |

---

## Autenticación

### `POST /api/v1/auth/register`

Registra un nuevo cliente en un tenant existente y crea su cuenta bancaria automáticamente.

**Request:**
```json
{
  "tenantSlug": "mi-banco",
  "fullName": "Juan Pérez",
  "documentNumber": "123456789",
  "email": "juan@example.com",
  "password": "Cliente123"
}
```

**Response (201):**
```json
{
  "success": true,
  "code": "CLIENT_REGISTERED",
  "description": "Cliente registrado correctamente. Debe iniciar sesion para obtener token.",
  "user": {
    "identityUserId": "guid",
    "userId": "guid",
    "tenantId": "guid",
    "fullName": "Juan Pérez",
    "documentNumber": "123456789",
    "email": "juan@example.com",
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "guid",
    "accountNumber": "6378534290",
    "ownerId": "guid",
    "balance": 0,
    "currency": "COP",
    "status": "ACTIVE",
    "createdAt": "2026-06-10T12:00:00Z"
  }
}
```

**Errores:**
- `400 DOCUMENT_REQUIRED` — Documento obligatorio
- `404 TENANT_NOT_FOUND` — Slug de tenant no existe
- `409 EMAIL_ALREADY_EXISTS` — Email ya registrado en el tenant
- `409 DOCUMENT_ALREADY_EXISTS` — Documento ya registrado en el tenant

---

### `POST /api/v1/auth/login`

Inicia sesión y obtiene un token JWT.

**Request:**
```json
{
  "tenantSlug": "mi-banco",
  "email": "juan@example.com",
  "password": "Cliente123"
}
```

**Response (200):**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "expiresAt": "2026-06-10T13:00:00Z",
  "user": {
    "identityUserId": "guid",
    "userId": "guid",
    "tenantId": "guid",
    "fullName": "Juan Pérez",
    "documentNumber": "123456789",
    "email": "juan@example.com",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "guid",
    "accountNumber": "6378534290",
    "ownerId": "guid",
    "balance": 500000.00,
    "currency": "COP",
    "status": "ACTIVE",
    "createdAt": "2026-06-10T12:00:00Z"
  }
}
```

**Errores:**
- `401 INVALID_TENANT_CREDENTIALS` — Credenciales inválidas

---

### `GET /api/v1/auth/me`

Obtiene los datos del usuario autenticado.

**Headers:**
```
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "user": {
    "identityUserId": "guid",
    "userId": "guid",
    "tenantId": "guid",
    "fullName": "Juan Pérez",
    "documentNumber": "123456789",
    "email": "juan@example.com",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "guid",
    "accountNumber": "6378534290",
    "ownerId": "guid",
    "balance": 500000.00,
    "currency": "COP",
    "status": "ACTIVE",
    "createdAt": "2026-06-10T12:00:00Z"
  }
}
```

---

## Tenants

### `GET /api/v1/tenants`

Lista todos los tenants disponibles. Endpoint público.

**Response (200):**
```json
{
  "tenants": [
    {
      "id": "guid",
      "name": "Mi Banco",
      "slug": "mi-banco",
      "mainCurrency": "COP",
      "maxTransactionAmount": 5000000.00,
      "feeType": "FIXED",
      "feeValue": 2000.00,
      "createdAt": "2026-06-10T12:00:00Z"
    }
  ]
}
```

---

### `POST /api/v1/tenants`

Crea un nuevo tenant con su administrador inicial. Endpoint público.

**Request:**
```json
{
  "nombreTenant": "Mi Banco",
  "correo": "admin@mibanco.com"
}
```

**Response (201):**
```json
{
  "tenant": {
    "id": "guid",
    "name": "Mi Banco",
    "slug": "mi-banco",
    "mainCurrency": "COP",
    "maxTransactionAmount": 5000000.00,
    "feeType": "FIXED",
    "feeValue": 0,
    "webhookUrl": null,
    "createdAt": "2026-06-10T12:00:00Z",
    "updatedAt": "2026-06-10T12:00:00Z"
  },
  "admin": {
    "id": "guid",
    "tenantId": "guid",
    "fullName": "Administrador Mi Banco",
    "email": "admin@mibanco.com",
    "role": "ADMIN",
    "temporaryPassword": "Admin...a1",
    "createdAt": "2026-06-10T12:00:00Z"
  }
}
```

---

## Cuentas

### `POST /api/v1/accounts`

Crea una cuenta bancaria para el cliente autenticado.

**Headers:**
```
Authorization: Bearer <token>
```

**Request:**
```json
{}
```

**Response (201):**
```json
{
  "success": true,
  "code": "ACCOUNT_CREATED",
  "description": "Cuenta creada correctamente",
  "data": {
    "id": "guid",
    "accountNumber": "6378534290",
    "ownerId": "guid",
    "fullName": "Juan Pérez",
    "documentNumber": "123456789",
    "balance": 0,
    "currency": "COP",
    "status": "ACTIVE",
    "createdAt": "2026-06-10T12:00:00Z"
  }
}
```

---

### `DELETE /api/v1/accounts/{accountId}`

Desactiva una cuenta (soft delete). Cambia estado a `INACTIVE`.

**Headers:**
```
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "success": true,
  "code": "ACCOUNT_DEACTIVATED",
  "description": "Cuenta desactivada correctamente",
  "data": {
    "id": "guid",
    "accountNumber": "6378534290",
    "ownerId": "guid",
    "fullName": "Juan Pérez",
    "documentNumber": "123456789",
    "balance": 500000.00,
    "currency": "COP",
    "status": "INACTIVE",
    "createdAt": "2026-06-10T12:00:00Z",
    "updatedAt": "2026-06-10T14:00:00Z",
    "deactivatedAt": "2026-06-10T14:00:00Z"
  }
}
```

---

### `POST /api/v1/accounts/recharge`

Recarga (deposita) fondos en la cuenta del cliente autenticado.

**Headers:**
```
Authorization: Bearer <token>
```

**Request:**
```json
{
  "amount": 100000.00
}
```

**Response (200):**
```json
{
  "success": true,
  "code": "ACCOUNT_RECHARGED",
  "description": "Cuenta recargada correctamente",
  "data": {
    "id": "guid",
    "accountNumber": "6378534290",
    "balance": 600000.00,
    "currency": "COP",
    "status": "ACTIVE"
  }
}
```

---

## Transferencias

### `POST /api/v1/accounts/transfer`

Transfiere fondos a otro cliente dentro del mismo tenant. Requiere `Idempotency-Key`.

**Headers:**
```
Authorization: Bearer <token>
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
X-Correlation-ID: 660e8400-e29b-41d4-a716-446655440001
```

**Request:**
```json
{
  "destinationDocumentNumber": "987654321",
  "amount": 50000.00
}
```

**Response (200):**
```json
{
  "transactionId": "guid",
  "status": "SUCCESS",
  "sourceAccountId": "guid",
  "destinationAccountId": "guid",
  "destinationDocumentNumber": "987654321",
  "amount": 50000.00,
  "feeAmount": 2000.00,
  "sourceBalance": 448000.00,
  "destinationBalance": 150000.00,
  "createdAt": "2026-06-10T12:00:00Z"
}
```

**Errores:**
- `400` — Monto excede límite, saldo insuficiente, cuenta inactiva
- `423` — Transacción en progreso (misma Idempotency-Key)

---

## Depósitos

### `POST /api/v1/accounts/deposit`

Deposita fondos en una cuenta destino. Aplica comisión configurada.

**Headers:**
```
Authorization: Bearer <token>
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
X-Correlation-ID: 660e8400-e29b-41d4-a716-446655440001
```

**Request:**
```json
{
  "destinationAccountId": "guid",
  "amount": 100000.00
}
```

**Response (200):**
```json
{
  "transactionId": "guid",
  "status": "SUCCESS",
  "originalAmount": 100000.00,
  "feeAmount": 2000.00,
  "createdAt": "2026-06-10T12:00:00Z"
}
```

---

## Retiros

### `POST /api/v1/accounts/withdraw`

Retira fondos de una cuenta origen. Aplica comisión configurada.

**Headers:**
```
Authorization: Bearer <token>
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
X-Correlation-ID: 660e8400-e29b-41d4-a716-446655440001
```

**Request:**
```json
{
  "sourceAccountId": "guid",
  "amount": 50000.00
}
```

**Response (200):**
```json
{
  "transactionId": "guid",
  "status": "SUCCESS",
  "originalAmount": 50000.00,
  "feeAmount": 1000.00,
  "createdAt": "2026-06-10T12:00:00Z"
}
```

---

## Historial de Transacciones

### `GET /api/v1/accounts/transactions`

Obtiene el historial paginado de transacciones del cliente autenticado.

**Headers:**
```
Authorization: Bearer <token>
```

**Query Parameters:**
| Parámetro | Tipo | Default | Descripción |
|-----------|------|---------|-------------|
| `limit` | int | 20 | Máximo 100 |
| `offset` | int | 0 | |
| `from` | datetime | - | Fecha inicio |
| `to` | datetime | - | Fecha fin |
| `type` | string | - | `DEPOSIT`, `WITHDRAW`, `TRANSFER` |

**Response (200):**
```json
{
  "success": true,
  "code": "TRANSACTION_HISTORY",
  "description": "Historial consultado correctamente",
  "data": {
    "limit": 20,
    "offset": 0,
    "total": 1,
    "items": [
      {
        "id": "guid",
        "type": "TRANSFER",
        "originalAmount": 50000.00,
        "feeAmount": 2000.00,
        "sourceAccountId": "guid",
        "destinationAccountId": "guid",
        "status": "SUCCESS",
        "createdAt": "2026-06-10T12:00:00Z"
      }
    ]
  }
}
```

---

## Idempotencia — Comportamiento

### Primer Request (key: `X`)
```
POST /api/v1/accounts/transfer
Idempotency-Key: X
```
→ `200 OK` (procesa, descuenta saldo, asocia key `X`)

### Segundo Request (misma key `X`)
```
POST /api/v1/accounts/transfer
Idempotency-Key: X
```
→ `200 OK` (misma respuesta exacta, NO descuenta saldo)

### Request Concurrente (misma key `X`)
```
Request A y Request B enviados simultáneamente con key X
```
→ Uno gana (`200 OK`), el otro recibe `423 Locked`

### Request con key fallida previamente
```
Primero falla con key X (saldo insuficiente)
Luego se reintenta con key X (fondos suficientes)
```
→ `200 OK` en el reintento (estado `FAILED` permite nuevo lock)

---

## Resumen de Endpoints

| Método | Ruta | Auth | Idempotencia | Descripción |
|--------|------|------|--------------|-------------|
| POST | /api/v1/auth/register | No | No | Registrar cliente |
| POST | /api/v1/auth/login | No | No | Iniciar sesión |
| GET | /api/v1/auth/me | Sí | No | Perfil actual |
| GET | /api/v1/tenants | No | No | Listar tenants |
| POST | /api/v1/tenants | No | No | Crear tenant + admin |
| POST | /api/v1/accounts | Sí | No | Crear cuenta bancaria |
| DELETE | /api/v1/accounts/{id} | Sí | No | Desactivar cuenta |
| POST | /api/v1/accounts/recharge | Sí | No | Recargar fondos |
| POST | /api/v1/accounts/transfer | Sí | Sí | Transferir fondos |
| POST | /api/v1/accounts/deposit | Sí | Sí | Depositar fondos |
| POST | /api/v1/accounts/withdraw | Sí | Sí | Retirar fondos |
| GET | /api/v1/accounts/transactions | Sí | No | Historial paginado |

## Claims del Token JWT

```json
{
  "sub": "identity_user_id",
  "email": "user@example.com",
  "jti": "unique_token_id",
  "tenant_id": "guid",
  "user_id": "guid",
  "tenant_role": "CLIENT",
  "role": ["CLIENT"],
  "exp": 1749600000,
  "iss": "HydraBankApi",
  "aud": "HydraBankClient"
}
```
