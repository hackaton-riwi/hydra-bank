# Hydra BankOS — Documentación de la API

> **Base URL:** `https://<tu-dominio>/api/v1`  
> **Formato:** Todos los endpoints reciben y devuelven `application/json`  
> **Autenticación:** Bearer Token JWT en el header `Authorization: Bearer <token>`

---

## Índice

1. [Conceptos clave](#conceptos-clave)
2. [Autenticación — `/api/v1/auth`](#1-autenticación)
3. [Tenants — `/api/v1/tenants`](#2-tenants)
4. [Routing path-based por tenant](#3-routing-path-based-por-tenant)
5. [Cuentas — `/api/v1/accounts`](#4-cuentas)
6. [Transacciones financieras](#5-transacciones-financieras)
7. [Códigos de error comunes](#6-códigos-de-error-comunes)
8. [Headers especiales para operaciones financieras](#7-headers-especiales)

---

## Conceptos clave

### TenantKey
Identificador de un tenant. Puede pasarse de tres formas:
- **Short ID:** `TEN-4A3F21BC` (formato `TEN-` seguido de los primeros 8 caracteres del GUID en mayúscula)
- **Slug:** `mi-banco` (nombre del tenant en minúsculas con guiones)
- **GUID completo:** `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`

### AccountKey
Identificador de una cuenta. Puede pasarse de dos formas:
- **Short ID:** `ACC-1234ABCD` (formato `ACC-` + primeros 8 chars del GUID)
- **GUID completo:** `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`

### Short IDs
El sistema genera IDs cortos con el patrón `PREFIJO-8CHARS`. Los prefijos son:
| Prefijo | Entidad |
|---------|---------|
| `TEN-` | Tenant |
| `USR-` | Usuario |
| `ACC-` | Cuenta |
| `TRX-` | Transacción |
| `LOG-` | Audit Log |

### Roles
| Rol | Permisos |
|-----|----------|
| `SUPERADMIN` | Acceso total a todos los tenants |
| `ADMIN` | Acceso de administración a su propio tenant |
| `CLIENT` | Acceso a sus propias cuentas y transacciones |

---

## 1. Autenticación

### POST `/api/v1/auth/register`
Registra un nuevo cliente dentro de un tenant. **No requiere autenticación.** También crea automáticamente una cuenta bancaria para el nuevo usuario.

**Request Body:**
```json
{
  "tenantSlug": "mi-banco",
  "fullName": "Juan Pérez García",
  "documentNumber": "1020304050",
  "email": "juan@ejemplo.com",
  "password": "miPassword123"
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `tenantSlug` | string (máx. 50) | ✅ | Slug del tenant al que pertenecerá el cliente. Ej: `mi-banco`. Se obtiene de `GET /api/v1/tenants` |
| `fullName` | string (máx. 150) | ✅ | Nombre completo del usuario |
| `documentNumber` | string (máx. 64) | ✅ | Número de documento de identidad. Se normaliza a mayúsculas. Debe ser único dentro del tenant |
| `email` | string email (máx. 150) | ✅ | Correo electrónico. Debe ser único dentro del tenant |
| `password` | string (mín. 6) | ✅ | Contraseña del usuario |

**Respuesta exitosa `201 Created`:**
```json
{
  "success": true,
  "code": "CLIENT_REGISTERED",
  "description": "Cliente registrado correctamente. Debe iniciar sesión para obtener token.",
  "user": {
    "id": "USR-4A3F21BC",
    "tenantId": "TEN-9B2C87DE",
    "FullName": "Juan Pérez García",
    "DocumentNumber": "1020304050",
    "Email": "juan@ejemplo.com",
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "Balance": 0.00,
    "Status": "ACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z"
  }
}
```

> ⚠️ **Importante:** El registro no devuelve un token JWT. Después de registrarse, el usuario debe hacer `POST /api/v1/auth/login` para obtener su token.

---

### POST `/api/v1/auth/login`
Autentica un usuario y devuelve un JWT. **No requiere autenticación.**

**Request Body:**
```json
{
  "tenantSlug": "mi-banco",
  "email": "juan@ejemplo.com",
  "password": "miPassword123"
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `tenantSlug` | string (máx. 50) | ✅ | Slug del tenant. Sin esto el sistema no puede identificar al usuario (dos tenants distintos pueden tener el mismo email) |
| `email` | string email | ✅ | Correo del usuario |
| `password` | string | ✅ | Contraseña |

**Respuesta exitosa `200 OK`:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2025-06-11T11:30:00Z",
  "user": {
    "id": "USR-4A3F21BC",
    "tenantId": "TEN-9B2C87DE",
    "fullName": "Juan Pérez García",
    "documentNumber": "1020304050",
    "email": "juan@ejemplo.com",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "Balance": 150000.00,
    "Currency": "COP",
    "Status": "ACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z"
  }
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `token` | JWT Bearer token. Usarlo en el header `Authorization: Bearer <token>` para todas las llamadas protegidas |
| `expiresAt` | Fecha/hora UTC en que vence el token |
| `user.id` | Short ID del usuario (`USR-XXXXXXXX`) |
| `user.tenantId` | Short ID del tenant al que pertenece (`TEN-XXXXXXXX`) |
| `user.roles` | Roles de identidad (sistema de autenticación): `["CLIENT"]`, `["ADMIN"]`, etc. |
| `user.tenantRole` | Rol dentro del banco: `CLIENT`, `ADMIN` o `SUPERADMIN` |
| `account` | Cuenta bancaria asociada. Puede ser `null` si el usuario no tiene cuenta aún |

---

### GET `/api/v1/auth/me`
Devuelve la información del usuario autenticado según el token. **Requiere autenticación.**

**Headers:**
```
Authorization: Bearer <token>
```

**Respuesta exitosa `200 OK`:**
```json
{
  "user": {
    "id": "USR-4A3F21BC",
    "tenantId": "TEN-9B2C87DE",
    "fullName": "Juan Pérez García",
    "documentNumber": "1020304050",
    "email": "juan@ejemplo.com",
    "roles": ["CLIENT"],
    "tenantRole": "CLIENT"
  },
  "account": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "Balance": 150000.00,
    "Currency": "COP",
    "Status": "ACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z"
  }
}
```

---

## 2. Tenants

### GET `/api/v1/tenants`
Lista todos los tenants registrados. **No requiere autenticación.**

**Respuesta exitosa `200 OK`:**
```json
{
  "tenants": [
    {
      "id": "TEN-9B2C87DE",
      "Name": "Mi Banco",
      "Slug": "mi-banco",
      "MainCurrency": "COP",
      "MaxTransactionAmount": 5000000.00,
      "FeeType": "FIXED",
      "FeeValue": 0.00,
      "CreatedAt": "2025-06-01T08:00:00Z"
    }
  ]
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `id` | Short ID del tenant (`TEN-XXXXXXXX`) |
| `Slug` | Identificador textual único del tenant. Se usa en login y registro |
| `MainCurrency` | Moneda principal, siempre `COP` |
| `MaxTransactionAmount` | Monto máximo permitido por transacción (por defecto 5.000.000) |
| `FeeType` | Tipo de comisión: `FIXED` (valor fijo) o `PERCENTAGE` (porcentaje) |
| `FeeValue` | Valor de la comisión (por defecto 0) |

---

### POST `/api/v1/tenants`
Crea un nuevo tenant junto con su usuario administrador. **No requiere autenticación.**

**Request Body:**
```json
{
  "nombreTenant": "Mi Banco",
  "correo": "admin@mibanco.com",
  "password": "adminPass123"
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `nombreTenant` | string (máx. 100) | ✅ | Nombre del tenant. De aquí se genera automáticamente el `slug` (ej: "Mi Banco" → `mi-banco`) |
| `correo` | string email (máx. 150) | ✅ | Correo del administrador del tenant |
| `password` | string (mín. 6) | ✅ | Contraseña del administrador |

**Respuesta exitosa `201 Created`:**
```json
{
  "tenant": {
    "id": "TEN-9B2C87DE",
    "Name": "Mi Banco",
    "Slug": "mi-banco",
    "MainCurrency": "COP",
    "MaxTransactionAmount": 5000000.00,
    "FeeType": "FIXED",
    "FeeValue": 0.00,
    "WebhookUrl": null,
    "CreatedAt": "2025-06-11T10:00:00Z",
    "UpdatedAt": "2025-06-11T10:00:00Z"
  },
  "admin": {
    "id": "USR-7F2A11CC",
    "tenantId": "TEN-9B2C87DE",
    "FullName": "Administrador Mi Banco",
    "Email": "admin@mibanco.com",
    "Role": "ADMIN",
    "CreatedAt": "2025-06-11T10:00:00Z"
  }
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `tenant.Slug` | Guárdalo: se usa en `tenantSlug` del login y registro |
| `tenant.WebhookUrl` | URL de notificación de transacciones (se configura después). Inicialmente `null` |
| `admin.id` | Short ID del admin creado automáticamente |

---

### GET `/api/v1/tenants/{tenantKey}/users`
Lista todos los usuarios del tenant con sus cuentas. **Requiere rol `ADMIN` o `SUPERADMIN`.**

**Path params:**
| Param | Descripción |
|-------|-------------|
| `tenantKey` | Short ID (`TEN-XXXXXXXX`), slug (`mi-banco`) o GUID completo del tenant |

**Respuesta exitosa `200 OK`:**
```json
{
  "tenant": {
    "id": "TEN-9B2C87DE",
    "Name": "Mi Banco",
    "Slug": "mi-banco",
    "MainCurrency": "COP",
    "MaxTransactionAmount": 5000000.00,
    "FeeType": "FIXED",
    "FeeValue": 0.00
  },
  "totalUsers": 3,
  "totalBalance": 450000.00,
  "users": [
    {
      "id": "USR-4A3F21BC",
      "tenantId": "TEN-9B2C87DE",
      "FullName": "Juan Pérez García",
      "DocumentNumber": "1020304050",
      "Email": "juan@ejemplo.com",
      "Role": "CLIENT",
      "CreatedAt": "2025-06-11T10:30:00Z",
      "Accounts": [
        {
          "id": "ACC-1234ABCD",
          "AccountNumber": "3748291056",
          "Balance": 150000.00,
          "Currency": "COP",
          "Status": "ACTIVE",
          "CreatedAt": "2025-06-11T10:30:00Z"
        }
      ]
    }
  ]
}
```

---

### GET `/api/v1/tenants/{tenantKey}/transactions`
Historial de transacciones del tenant con filtros. **Requiere rol `ADMIN` o `SUPERADMIN`.**

**Query params opcionales:**
| Param | Tipo | Descripción |
|-------|------|-------------|
| `from` | datetime | Fecha/hora inicio del filtro (UTC). Ej: `2025-06-01T00:00:00Z` |
| `to` | datetime | Fecha/hora fin del filtro (UTC) |
| `type` | string | Tipo de transacción: `TRANSFER`, `DEPOSIT` o `WITHDRAW` |
| `limit` | int (1–200) | Cantidad de resultados por página. Por defecto `50` |
| `offset` | int | Posición de inicio para paginación. Por defecto `0` |

**Respuesta exitosa `200 OK`:**
```json
{
  "tenant": { "id": "TEN-9B2C87DE", "Name": "Mi Banco", "Slug": "mi-banco", "..." : "..." },
  "limit": 50,
  "offset": 0,
  "total": 120,
  "totalMoved": 8500000.00,
  "totalFees": 0.00,
  "items": [
    {
      "id": "TRX-AA11BB22",
      "userId": "USR-4A3F21BC",
      "UserName": "Juan Pérez García",
      "UserDocument": "1020304050",
      "Type": "TRANSFER",
      "OriginalAmount": 100000.00,
      "FeeAmount": 0.00,
      "sourceAccountId": "ACC-1234ABCD",
      "destinationAccountId": "ACC-5678EFGH",
      "Status": "SUCCESS",
      "CorrelationId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
      "CreatedAt": "2025-06-11T14:00:00Z"
    }
  ]
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `totalMoved` | Suma de montos originales de transacciones exitosas en el rango |
| `totalFees` | Suma de comisiones cobradas en transacciones exitosas |
| `items[].Type` | `TRANSFER`, `DEPOSIT` o `WITHDRAW` |
| `items[].Status` | `SUCCESS` o `FAILED` |
| `items[].CorrelationId` | UUID de correlación para trazabilidad. Viene del header `X-Correlation-ID` |

---

### GET `/api/v1/tenants/{tenantKey}/audit-logs`
Historial de auditoría del tenant. **Requiere rol `ADMIN` o `SUPERADMIN`.**

**Query params opcionales:**
| Param | Tipo | Descripción |
|-------|------|-------------|
| `limit` | int (1–200) | Por defecto `50` |
| `offset` | int | Por defecto `0` |

**Respuesta exitosa `200 OK`:**
```json
{
  "tenant": { "id": "TEN-9B2C87DE", "..." : "..." },
  "limit": 50,
  "offset": 0,
  "total": 45,
  "logs": [
    {
      "id": "LOG-CC33DD44",
      "userId": "USR-4A3F21BC",
      "UserName": "Juan Pérez García",
      "Action": "TRANSFER",
      "OldValue": "{\"SourceAccountId\": \"...\", \"SourcePreviousBalance\": 250000}",
      "NewValue": "{\"SourceBalance\": 150000, \"Amount\": 100000, \"Fee\": 0}",
      "CreatedAt": "2025-06-11T14:00:00Z"
    }
  ]
}
```

| `Action` posibles | Descripción |
|-------------------|-------------|
| `CLIENT_REGISTERED` | Registro de nuevo cliente |
| `TENANT_CREATED` | Creación de tenant |
| `ACCOUNT_CREATED` | Creación de cuenta |
| `ACCOUNT_DEACTIVATED` | Desactivación de cuenta |
| `ACCOUNT_RECHARGED` | Recarga de cuenta |
| `TRANSFER` | Transferencia entre cuentas |
| `DEPOSIT` | Depósito |
| `WITHDRAW` | Retiro |

---

### Endpoints equivalentes con tenant del token autenticado

Estos endpoints son iguales a los anteriores pero usan el tenant_id del token JWT en lugar de un `{tenantKey}` en la URL. Útiles para admins que ya están autenticados.

| Endpoint | Equivale a |
|----------|------------|
| `GET /api/v1/tenants/current/users` | `GET /api/v1/tenants/{miTenantId}/users` |
| `GET /api/v1/tenants/current/transactions` | `GET /api/v1/tenants/{miTenantId}/transactions` |
| `GET /api/v1/tenants/current/audit-logs` | `GET /api/v1/tenants/{miTenantId}/audit-logs` |

---

### DELETE `/api/v1/tenants/{tenantKey}`
Elimina un tenant completo con todos sus usuarios, cuentas, transacciones y registros. **Requiere rol `SUPERADMIN`.**

**Respuesta exitosa `200 OK`:**
```json
{
  "success": true,
  "code": "TENANT_DELETED",
  "description": "Tenant eliminado con usuarios, cuentas, transacciones, logs e identidades de autenticación",
  "tenant": { "id": "TEN-9B2C87DE", "Name": "Mi Banco", "..." : "..." },
  "deleted": {
    "users": 5,
    "accounts": 5,
    "transactions": 120,
    "auditLogs": 45,
    "idempotencyRecords": 80
  }
}
```

> ⚠️ **Esta operación es irreversible.** Elimina en cascada toda la información del tenant.

---

## 3. Routing path-based por tenant

Todas las rutas de tenant admiten dos formatos equivalentes. El formato path-based usa el slug en la URL y es el recomendado para producción y demos.

Todas las rutas de tenant admiten dos formatos equivalentes. El formato path-based usa el slug en la URL y es el recomendado para producción y demos.

### Formato 1: path-based (recomendado)
```
/api/v1/{tenantSlug}/users
/api/v1/{tenantSlug}/transactions
/api/v1/{tenantSlug}/audit-logs
/api/v1/{tenantSlug}                    (DELETE, solo SUPERADMIN)
/api/v1/{tenantSlug}/auth/login
/api/v1/{tenantSlug}/auth/register
```

### Formato 2: legacy con tenantKey
```
/api/v1/tenants/{tenantKey}/users
/api/v1/tenants/{tenantKey}/transactions
/api/v1/tenants/{tenantKey}/audit-logs
/api/v1/tenants/{tenantKey}             (DELETE, solo SUPERADMIN)
/api/v1/auth/login                      (con tenantSlug en body)
/api/v1/auth/register                   (con tenantSlug en body)
```

> Ambos formatos devuelven exactamente la misma respuesta. El path-based elimina la necesidad de enviar `tenantSlug` en el body de login/register.

### Slugs reservados
Los siguientes slugs no pueden usarse porque colisionan con rutas del sistema:

| Slug reservado | Motivo |
|----------------|--------|
| `auth` | Ruta de autenticación |
| `accounts` | Ruta de cuentas |
| `tenants` | Ruta de tenants |
| `health` | Reservado para health checks |
| `swagger` | Reservado para documentación |

Si un slug generado automáticamente coincide con uno reservado, la API rechaza la creación del tenant.

---

## 4. Cuentas

> Todos los endpoints de `/api/v1/accounts` requieren autenticación con rol `CLIENT`.

### POST `/api/v1/accounts`
Crea una cuenta bancaria para el cliente autenticado. **Cada cliente solo puede tener una cuenta.**

**Request Body:** vacío `{}` (no se requieren campos)

**Respuesta exitosa `201 Created`:**
```json
{
  "success": true,
  "code": "ACCOUNT_CREATED",
  "description": "Cuenta creada correctamente",
  "data": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "FullName": "Juan Pérez García",
    "DocumentNumber": "1020304050",
    "Balance": 0.00,
    "Currency": "COP",
    "Status": "ACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z"
  }
}
```

> ℹ️ Al registrarse con `POST /api/v1/auth/register`, ya se crea una cuenta automáticamente. Este endpoint es para casos donde el usuario no tiene cuenta todavía.

---

### DELETE `/api/v1/accounts/{accountKey}`
Desactiva la cuenta del cliente autenticado.

**Path params:**
| Param | Descripción |
|-------|-------------|
| `accountKey` | Short ID (`ACC-1234ABCD`) o GUID de la cuenta |

**Respuesta exitosa `200 OK`:**
```json
{
  "success": true,
  "code": "ACCOUNT_DEACTIVATED",
  "description": "Cuenta desactivada correctamente",
  "data": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "FullName": "Juan Pérez García",
    "DocumentNumber": "1020304050",
    "Balance": 150000.00,
    "Currency": "COP",
    "Status": "INACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z",
    "UpdatedAt": "2025-06-11T15:00:00Z",
    "DeactivatedAt": "2025-06-11T15:00:00Z"
  }
}
```

---

### POST `/api/v1/accounts/recharge`
Recarga (añade saldo) a la cuenta propia del cliente autenticado. No genera comisión.

**Request Body:**
```json
{
  "amount": 50000.00
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `amount` | decimal | ✅ | Monto a recargar. Debe ser mayor que 0 |

**Respuesta exitosa `200 OK`:**
```json
{
  "success": true,
  "code": "ACCOUNT_RECHARGED",
  "description": "Cuenta recargada correctamente",
  "data": {
    "id": "ACC-1234ABCD",
    "AccountNumber": "3748291056",
    "ownerId": "USR-4A3F21BC",
    "FullName": "Juan Pérez García",
    "DocumentNumber": "1020304050",
    "Balance": 200000.00,
    "Currency": "COP",
    "Status": "ACTIVE",
    "CreatedAt": "2025-06-11T10:30:00Z",
    "UpdatedAt": "2025-06-11T15:30:00Z",
    "DeactivatedAt": null
  }
}
```

---

### GET `/api/v1/accounts/transactions`
Historial de transacciones del cliente autenticado. No recibe query params; devuelve las transacciones propias mas recientes, con limite interno de 100 resultados.

**Respuesta exitosa `200 OK`:**
```json
{
  "success": true,
  "code": "TRANSACTION_HISTORY",
  "description": "Historial consultado correctamente",
  "data": {
    "limit": 100,
    "total": 35,
    "items": [
      {
        "id": "TRX-AA11BB22",
        "Type": "TRANSFER",
        "OriginalAmount": 100000.00,
        "FeeAmount": 0.00,
        "sourceAccountId": "ACC-1234ABCD",
        "destinationAccountId": "ACC-5678EFGH",
        "Status": "SUCCESS",
        "CreatedAt": "2025-06-11T14:00:00Z"
      }
    ]
  }
}
```

---

## 4. Transacciones Financieras

> Todos los endpoints de transacciones bajo `/api/v1/accounts` requieren rol `CLIENT`.
> Las operaciones financieras soportan **idempotencia** para evitar duplicados.

### Headers requeridos para operaciones financieras

| Header | Tipo | Obligatorio | Descripción |
|--------|------|-------------|-------------|
| `Authorization` | string | ✅ | `Bearer <token>` |
| `Idempotency-Key` | UUID | ✅ | UUID v4 único por operación. Si se repite la misma clave, se devuelve el resultado original en lugar de procesar de nuevo |
| `X-Correlation-ID` | UUID | ✅ | UUID v4 para trazabilidad y correlación entre sistemas |

> **¿Cómo funciona la idempotencia?** Si envías el mismo `Idempotency-Key` dos veces, la segunda llamada devuelve exactamente la misma respuesta que la primera, sin ejecutar la transacción de nuevo. Si la transacción aún está en progreso, recibirás `423 Locked`.

---

### POST `/api/v1/accounts/transfer`
Transfiere fondos de la cuenta del cliente autenticado a otro cliente del mismo tenant, identificado por su número de documento.

**Headers requeridos:** `Authorization`, `Idempotency-Key`, `X-Correlation-ID`

**Request Body:**
```json
{
  "destinationDocumentNumber": "9876543210",
  "amount": 100000.00
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `destinationDocumentNumber` | string (máx. 64) | ✅ | Número de documento del cliente **destino** dentro del mismo tenant. Se normaliza a mayúsculas. No puede ser el mismo usuario |
| `amount` | decimal | ✅ | Monto a transferir en COP. Debe ser mayor que 0 y no superar `MaxTransactionAmount` del tenant |

**Respuesta exitosa `200 OK`:**
```json
{
  "id": "TRX-AA11BB22",
  "TransactionShortId": "TRX-AA11BB22",
  "Status": "SUCCESS",
  "SourceAccountShortId": "ACC-1234ABCD",
  "DestinationAccountShortId": "ACC-5678EFGH",
  "DestinationDocumentNumber": "9876543210",
  "Amount": 100000.00,
  "FeeAmount": 0.00,
  "SourceBalance": 50000.00,
  "DestinationBalance": 250000.00,
  "CreatedAt": "2025-06-11T14:00:00Z"
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `id` | Short ID de la transacción (`TRX-XXXXXXXX`) |
| `Status` | Siempre `SUCCESS` si no hubo error |
| `SourceAccountShortId` | Short ID de la cuenta origen (la del usuario autenticado) |
| `DestinationAccountShortId` | Short ID de la cuenta destino |
| `Amount` | Monto transferido |
| `FeeAmount` | Comisión cobrada (calculada según configuración del tenant) |
| `SourceBalance` | Saldo restante en la cuenta origen después de la operación |
| `DestinationBalance` | Saldo de la cuenta destino después de recibir |

> **Nota sobre comisiones en transferencia:** El `FeeAmount` se descuenta adicional al monto. El origen paga `Amount + FeeAmount`. El destino recibe exactamente `Amount`.

**Header de respuesta:**
```
X-Correlation-ID: <el mismo UUID que enviaste>
```

---

### POST `/api/v1/accounts/deposit`
Deposita fondos en una cuenta específica del tenant (no necesariamente la propia).

**Headers requeridos:** `Authorization`, `Idempotency-Key`, `X-Correlation-ID`

**Request Body:**
```json
{
  "destinationAccountId": "ACC-1234ABCD",
  "amount": 200000.00
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `destinationAccountId` | string | ✅ | Short ID (`ACC-1234ABCD`) o GUID de la cuenta destino donde se depositarán los fondos |
| `amount` | decimal | ✅ | Monto a depositar. Debe ser mayor que 0. El monto neto acreditado es `amount - fee` |

**Respuesta exitosa `200 OK`:**
```json
{
  "id": "TRX-BB22CC33",
  "TransactionShortId": "TRX-BB22CC33",
  "DestinationAccountShortId": "ACC-1234ABCD",
  "Status": "SUCCESS",
  "OriginalAmount": 200000.00,
  "FeeAmount": 0.00,
  "NetAmount": 200000.00,
  "DestinationBalance": 350000.00,
  "CreatedAt": "2025-06-11T15:00:00Z"
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `OriginalAmount` | Monto bruto enviado en el request |
| `FeeAmount` | Comisión cobrada |
| `NetAmount` | Monto real acreditado en la cuenta destino (`OriginalAmount - FeeAmount`) |
| `DestinationBalance` | Saldo de la cuenta destino después del depósito |

> **Nota sobre comisiones en depósito:** La comisión se descuenta del monto recibido. Si depositas 200.000 y la comisión es 1.000, la cuenta recibe 199.000.

---

### POST `/api/v1/accounts/withdraw`
Retira fondos de la cuenta propia del cliente autenticado.

**Headers requeridos:** `Authorization`, `Idempotency-Key`, `X-Correlation-ID`

**Request Body:**
```json
{
  "sourceAccountId": "ACC-1234ABCD",
  "amount": 50000.00
}
```

| Campo | Tipo | Obligatorio | Descripción |
|-------|------|-------------|-------------|
| `sourceAccountId` | string | ✅ | Short ID (`ACC-1234ABCD`) o GUID de la cuenta de donde se retiran los fondos. Debe pertenecer al usuario autenticado |
| `amount` | decimal | ✅ | Monto a retirar. El débito total será `amount + fee` |

**Respuesta exitosa `200 OK`:**
```json
{
  "id": "TRX-CC33DD44",
  "TransactionShortId": "TRX-CC33DD44",
  "SourceAccountShortId": "ACC-1234ABCD",
  "Status": "SUCCESS",
  "OriginalAmount": 50000.00,
  "FeeAmount": 0.00,
  "TotalDebit": 50000.00,
  "SourceBalance": 100000.00,
  "CreatedAt": "2025-06-11T15:30:00Z"
}
```

| Campo respuesta | Descripción |
|----------------|-------------|
| `OriginalAmount` | Monto solicitado a retirar |
| `FeeAmount` | Comisión cobrada |
| `TotalDebit` | Débito total descontado de la cuenta (`OriginalAmount + FeeAmount`) |
| `SourceBalance` | Saldo restante después del retiro |

> **Nota sobre comisiones en retiro:** La comisión se cobra adicional al monto. Si retiras 50.000 y la comisión es 1.000, tu cuenta se debita 51.000.

---

## 5. Códigos de error comunes

Todos los errores siguen el formato:
```json
{
  "success": false,
  "code": "CODIGO_ERROR",
  "description": "Mensaje descriptivo del error"
}
```

| HTTP | Código | Situación |
|------|--------|-----------|
| 400 | `ACCOUNT_CREATE_FAILED` | No se pudo crear la cuenta (ya existe o el usuario no es CLIENT) |
| 400 | `TRANSFER_FAILED` | Transferencia fallida (saldo insuficiente, cuenta inactiva, documento no existe, etc.) |
| 400 | `DEPOSIT_FAILED` | Depósito fallido |
| 400 | `WITHDRAW_FAILED` | Retiro fallido |
| 401 | `INVALID_TENANT_CREDENTIALS` | Email, contraseña o tenant incorrecto en el login |
| 404 | `TENANT_NOT_FOUND` | El tenant indicado no existe |
| 409 | `EMAIL_ALREADY_EXISTS` | Ya existe un usuario con ese correo en el tenant |
| 409 | `DOCUMENT_ALREADY_EXISTS` | Ya existe un usuario con ese documento en el tenant |
| 423 | `IDEMPOTENCY_CONFLICT` | La transacción con ese `Idempotency-Key` ya está en progreso |

---

## 6. Headers especiales

### Para operaciones financieras (Transfer, Deposit, Withdraw)

```http
POST /api/v1/accounts/transfer
Authorization: Bearer eyJhbGci...
Content-Type: application/json
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
X-Correlation-ID: 6ba7b810-9dad-11d1-80b4-00c04fd430c8
```

- **`Idempotency-Key`** debe ser un UUID válido. Generar uno nuevo por cada operación nueva. Reusar el mismo key para reintentar en caso de error de red sin riesgo de duplicar la transacción.
- **`X-Correlation-ID`** debe ser un UUID válido. Se devuelve en el header de respuesta para rastrear la operación end-to-end.

---

## Flujo típico de integración

```
1. GET  /api/v1/tenants                    → Obtener el slug del tenant
2. POST /api/v1/auth/register              → Registrar usuario (crea cuenta automáticamente)
3. POST /api/v1/auth/login                 → Obtener JWT token
4. GET  /api/v1/auth/me                    → Verificar sesión y obtener accountId
5. POST /api/v1/accounts/recharge          → Cargar saldo inicial
6. POST /api/v1/accounts/transfer          → Transferir a otro usuario
7. GET  /api/v1/accounts/transactions      → Consultar historial propio
```

Para administradores:
```
1. POST /api/v1/tenants                    → Crear tenant
2. POST /api/v1/auth/login                 → Autenticarse como ADMIN
3. GET  /api/v1/tenants/current/users      → Ver todos los usuarios
4. GET  /api/v1/tenants/current/transactions → Ver todas las transacciones
5. GET  /api/v1/tenants/current/audit-logs → Ver logs de auditoría
```
