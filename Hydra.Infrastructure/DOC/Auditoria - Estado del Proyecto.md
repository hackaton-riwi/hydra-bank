# Auditoría del Backend — BankOS

## Estado vs Requerimientos de la Hackatón

| # | Requerimiento | Estado | Detalle |
|---|---|---|---|
| 1 | Registro de tenants | ✅ | `TenantsController` con validación de slug, FeeType y unicidad |
| 2 | Aislamiento estricto entre tenants | ✅ | `tenant_id` en todas las tablas, FK compuestas, RLS configurado |
| 3 | Configuración por tenant | ✅ | Límite, comisión, moneda principal, tasas de cambio |
| 4 | Webhook de notificación | ❌ | `WebhookUrl` existe en BD pero **nunca se envía HTTP POST** |
| 5 | Auth con JWT + claims | ✅ | `AuthController` con register/login/me, tenant_id, user_id, role en claims |
| 6 | Rate limiting | ✅ | 2 políticas: auth (5/min) y financial (30/min), por IP y userId |
| 7 | Auditoría inmutable | ⚠️ | `AuditLog` en `TransactionService` pero **ausente en `AccountService`** |
| 8 | Creación de cuentas | ✅ | `AccountsController` + `AccountService` |
| 9 | Soft delete de cuentas | ✅ | Cambia estado a INACTIVE/BLOCKED |
| 10 | Depósito, retiro, transferencia | ✅ | Dos implementaciones: `AccountService` y `TransactionService` |
| 11 | Multimoneda con tasas estáticas | ✅ | Conversión automática, tasas por tenant |
| 12 | Idempotencia con Redis SET NX | ✅ | Atómico, upsert en PostgreSQL, 24h de expiración |
| 13 | X-Correlation-ID | ⚠️ | Se lee del header y se persiste en BD, pero **no se loguea** (falta Serilog) |
| 14 | Historial con paginación | ✅ | `GET /api/v1/accounts/transactions` con limit/offset/filtros |
| 15 | API versionada (v1) | ⚠️ | 4/5 controladores usan `/api/v1/`. `TransactionsController` usa `/api/transactions` |
| 16 | CORS | ❌ | No hay `AddCors()` — la app móvil no podrá consumir la API |
| 17 | Docker Compose | ✅ | `docker-compose.yml` con app + postgres + redis |
| 18 | FAILED en enum idempotency_state | ⚠️ | Código tiene FAILED, pero la migración de BD solo tiene PROCESSING, COMPLETED |

---

## Problemas Detectados

### 🔴 Crítico: Dos implementaciones duplicadas de operaciones financieras

Actualmente coexisten **dos flujos completos** para hacer depósitos, retiros y transferencias:

```
Ruta A (AccountService):
  POST /api/v1/accounts/{id}/deposit
  POST /api/v1/accounts/{id}/withdraw
  POST /api/v1/accounts/transfer
  GET  /api/v1/accounts/transactions

Ruta B (TransactionService):
  POST /api/transactions/transfer      ← sin v1
  POST /api/transactions/deposit
  POST /api/transactions/withdraw
```

Ambos flujos tienen distinto mecanismo de idempotencia, distinta estructura de respuesta, distinto manejo de auditoría y distinto nivel de completitud.

---

## Comparativa Detallada: AccountService vs TransactionService

### Arquitectura

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Capa | `Application/Services` | `Application/Services` |
| Interfaz | `IAccountService` | `ITransactionService` |
| Controller | `AccountsController` | `TransactionsController` |
| Obtiene tenant/user | `IHttpContextAccessor` (acoplado a HTTP) | Recibe por parámetro del controller |
| Estructura de respuesta | `ServiceResponse(StatusCode, Body)` + wrapper `{ success, code, description, data }` | DTOs tipados directos (`TransferResponseDto`, etc.) |

### Idempotencia

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Mecanismo | Solo BD: inserta `PROCESSING`, catch `DbUpdateException` | Redis SET NX + BD upsert |
| Atómico | No — entre SELECT e INSERT hay ventana | Sí — `StringSetAsync(When.NotExists)` |
| Abstracción | Inline en `ExecuteIdempotentAsync` | `IIdempotencyService` separada |
| Request hash | ✅ SHA256, valida que payload no haya cambiado | ❌ No se valida (deuda documentada) |
| Expiración | 24h | 24h |
| Lock release | `COMPLETED` en DB | `KeyDeleteAsync` en Redis + `COMPLETED` en DB |

### Funcionalidad

| Característica | AccountService | TransactionService |
|---|---|---|
| Crear cuenta | ✅ | ❌ |
| Desactivar cuenta | ✅ | ❌ |
| Depósito | ✅ | ✅ |
| Retiro | ✅ | ✅ |
| Transferencia | ✅ | ✅ |
| Comisión en transferencia | ✅ (porcentual o fija) | ✅ (porcentual o fija) |
| Comisión en depósito | ❌ | ✅ (deduce del monto) |
| Comisión en retiro | ❌ | ✅ (suma al débito) |
| Conversión multimoneda | ✅ | ✅ |
| Tasa de cambio con caché | ✅ (IDistributedCache, 30 min) | ❌ (consulta directa cada vez) |
| Configuración tenant con caché | ✅ (IDistributedCache, 10 min) | ❌ (consulta directa cada vez) |
| Historial con paginación | ✅ | ❌ |
| Auditoría (AuditLog) | ❌ | ✅ |
| Correlation ID | ❌ | ✅ (header → BD → response) |

### Manejo de Errores

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Saldo insuficiente | `400 + INSUFFICIENT_FUNDS` | `500 InvalidOperationException` |
| Cuenta inactiva | `400 + ACCOUNT_NOT_ACTIVE` | `500 InvalidOperationException` |
| Key duplicada en progreso | `409 + TRANSACTION_IN_PROGRESS` | `423 Locked` |
| Key reusada con distinto payload | `409 + IDEMPOTENCY_KEY_REUSED` | No validado |
| Key expirada | `409 + IDEMPOTENCY_KEY_EXPIRED` | No validado |
| Formato de respuesta | Estandarizado `{ success, code, description, data }` | Sin wrapper |

### Integración con el resto del sistema

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Rate limiting | ✅ `[EnableRateLimiting("financial")]` | ❌ Sin atributo |
| Route versioning | ✅ `/api/v1/accounts` | ❌ `/api/transactions` |
| X-Correlation-ID en response | ❌ | ✅ |
| AuditLog en BD | ❌ | ✅ |
| IdempotencyService | ❌ (usa su propio mecanismo) | ✅ |
| Transacción Serializable | ✅ | ✅ |

---

## Veredicto

### ¿Cuál está más completa?

**AccountService** tiene más features: CRUD de cuentas, historial paginado, caché de tasas y config, validación de request hash, estructura de respuesta estandarizada.

### ¿Cuál tiene mejor arquitectura?

**TransactionService**. Usa `IIdempotencyService` como abstracción limpia, Redis para locks atómicos, propaga correlation ID, registra auditoría, maneja comisiones en los 3 tipos de operación, y el controlador es delgado sin `IHttpContextAccessor`.

### Recomendación

Unificar **tomando lo mejor de cada uno**:

| Qué tomar | De | Razón |
|---|---|---|
| Abstracción `IIdempotencyService` + Redis SET NX | TransactionService | Atómico, testeable, desacoplado |
| AuditLog en cada operación | TransactionService | Requisito obligatorio |
| X-Correlation-ID header → BD → response | TransactionService | Requisito obligatorio |
| Comisiones en depósitos y retiros | TransactionService | Consistencia financiera |
| CRUD de cuentas (crear, desactivar) | AccountService | Funcionalidad faltante |
| Historial paginado con filtros | AccountService | Requisito obligatorio |
| Caché de tasas de cambio y config | AccountService | Performance |
| Request hash validation | AccountService | Seguridad idempotencia |
| Formato respuesta `{ success, code, description, data }` | AccountService | Consistencia API |
| HTTP 409 para conflictos de idempotencia | AccountService | Semántica HTTP correcta |
| Controller sin IHttpContextAccessor | TransactionService | Menos acoplamiento |

---

## Checklist de Correcciones Prioritarias

- [ ] **Unificar flujos**: migrar AccountService a TransactionService o viceversa
- [ ] **Agregar Webhook**: HTTP POST a `Tenant.WebhookUrl` después de cada transferencia exitosa
- [ ] **Agregar CORS**: `AddCors()` con política permisiva para la app móvil
- [ ] **Agregar Serilog**: con enricher de correlation ID y request logging
- [ ] **Arreglar ruta**: `TransactionsController` → `/api/v1/transactions`
- [ ] **Generar migration**: agregar `FAILED` al enum PostgreSQL `idempotency_state`
- [ ] **Validar request hash** en `IdempotencyService.FailAsync` y `CompleteAsync`
- [ ] **Agregar rate limiting** a `TransactionsController`
