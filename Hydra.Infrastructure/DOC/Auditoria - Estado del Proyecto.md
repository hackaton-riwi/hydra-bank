# Auditoría del Backend — BankOS

## Estado vs Requerimientos de la Hackatón

| # | Requerimiento | Estado | Detalle |
|---|---|---|---|
| 1 | Registro de tenants | ✅ | `TenantsController` con validación de slug, FeeType y unicidad |
| 2 | Aislamiento estricto entre tenants | ✅ | `tenant_id` en todas las tablas, FK compuestas, RLS configurado |
| 3 | Configuración por tenant | ✅ | Límite, comisión, moneda principal, tasas de cambio |
| 4 | Webhook de notificación | ✅ | `IWebhookNotifier` + `WebhookNotifier` (fire-and-forget), POST asíncrono tras commit en transfer/deposit/withdraw |
| 5 | Auth con JWT + claims | ✅ | `AuthController` con register/login/me, tenant_id, user_id, role en claims |
| 6 | Rate limiting | ✅ | 2 políticas: auth (5/min) y financial (30/min), por IP y userId — **aplicado a ambos controladores** |
| 7 | Auditoría inmutable | ✅ | `AuditLog` en `TransactionService` (transfer/deposit/withdraw) + `AccountService` (CRUD + historial) |
| 8 | Creación de cuentas | ✅ | `AccountsController` + `AccountService` |
| 9 | Soft delete de cuentas | ✅ | Cambia estado a INACTIVE/BLOCKED |
| 10 | Depósito, retiro, transferencia | ✅ | **Unificado en `TransactionService`** (ruta principal), `AccountService` mantiene CRUD/historial |
| 11 | Multimoneda con tasas estáticas | ✅ | Conversión automática, tasas por tenant |
| 12 | Idempotencia con Redis SET NX | ✅ | Atómico (`StringSetAsync(When.NotExists)`), persistencia dual Redis+PostgreSQL, 24h expiración, validación request_hash |
| 13 | X-Correlation-ID | ✅ | Header → BD → Response header (`X-Correlation-ID`) en `TransactionsController` |
| 14 | Historial con paginación | ✅ | `GET /api/v1/accounts/transactions` con limit/offset/filtros |
| 15 | API versionada (v1) | ✅ | **Todos los controladores usan `/api/v1/`** (`TransactionsController` corregido) |
| 16 | CORS | ✅ | `AddCors()` + `UseCors()` política permisiva (any origin/header/method) |
| 17 | Docker Compose | ✅ | `docker-compose.yml` con app + postgres + redis |
| 18 | FAILED en enum idempotency_state | ✅ | Enum C# + migración SQL + mapeo EF Core incluyen `FAILED` |

---

## Problemas Detectados (ACTUALIZADO)

### 🟢 RESUELTO: Duplicación de flujos financieros

**Decisión tomada**: Mantener **dos rutas con responsabilidades separadas** (no unificación forzada):

| Ruta | Endpoints | Responsabilidad | Visibilidad |
|---|---|---|---|
| **AccountService** (Ruta A — **oficial**) | `POST /api/v1/accounts`<br>`DELETE /api/v1/accounts/{id}`<br>`POST /api/v1/accounts/recharge`<br>`GET /api/v1/accounts/transactions` | CRUD cuentas + recarga simple + historial paginado | ✅ Swagger / README |
| **TransactionService** (Ruta B — **core financiero**) | `POST /api/v1/transactions/deposit`<br>`POST /api/v1/transactions/withdraw`<br>`POST /api/v1/transactions/transfer` | Operaciones financieras completas: comisión, conversión, auditoría, idempotencia atómica, webhook, correlation-id | 🚫 Swagger (`[ApiExplorerSettings(IgnoreApi = true)]`) |

**Justificación**:
- Flutter consume `/api/v1/accounts/*` → **zero breaking changes**
- `TransactionService` expone la lógica robusta para uso interno/futuro
- Un solo flujo visible en Swagger evita confusión del jurado
- `AccountService.RechargeAsync` permanece para compatibilidad (recarga sin comisión)

---

## Comparativa Actualizada: AccountService vs TransactionService

### Arquitectura

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Capa | `Application/Services` | `Application/Services` |
| Interfaz | `IAccountService` | `ITransactionService` |
| Controller | `AccountsController` | `TransactionsController` (oculto en Swagger) |
| Obtiene tenant/user | `IHttpContextAccessor` | Parámetros del controller (delgado) |
| Estructura de respuesta | Wrapper `{ success, code, description, data }` | DTOs tipados (`DepositResponseDto`, etc.) |

### Idempotencia

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Mecanismo | DB-only: INSERT `PROCESSING` + catch `DbUpdateException` | **Redis SET NX + DB upsert** (dual persistence) |
| Atómico | No — ventana SELECT/INSERT | **Sí — `StringSetAsync(When.NotExists)`** |
| Abstracción | Inline en `ExecuteIdempotentAsync` | `IIdempotencyService` separada + inyectada |
| Request hash | ✅ SHA256 | ✅ SHA256 (validado en CompleteAsync/FailAsync) |
| Expiración | 24h | 24h (Redis) + 24h (DB) |
| Estados | PROCESSING/COMPLETED | PROCESSING/COMPLETED/FAILED |

### Funcionalidad

| Característica | AccountService | TransactionService |
|---|---|---|
| Crear cuenta | ✅ | ❌ |
| Desactivar cuenta | ✅ | ❌ |
| Depósito | ✅ (simple, sin comisión) | ✅ (con comisión + audit + webhook) |
| Retiro | ✅ (simple, sin comisión) | ✅ (con comisión + audit + webhook) |
| Transferencia | ✅ (simple, sin comisión) | ✅ (con comisión + audit + webhook) |
| Comisión en transferencia | ✅ | ✅ |
| Comisión en depósito | ❌ | ✅ (deduce del monto) |
| Comisión en retiro | ❌ | ✅ (suma al débito) |
| Conversión multimoneda | ✅ | ✅ |
| Tasa de cambio con caché | ✅ (IDistributedCache, 30 min) | ❌ (consulta directa — deuda técnica) |
| Configuración tenant con caché | ✅ (IDistributedCache, 10 min) | ❌ (consulta directa — deuda técnica) |
| Historial con paginación | ✅ | ❌ |
| Auditoría (AuditLog) | ❌ (solo CRUD) | ✅ (todas las ops financieras) |
| Correlation ID | ❌ | ✅ (header → BD → response header) |
| Webhook | ❌ | ✅ (fire-and-forget post-commit) |

### Manejo de Errores

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Saldo insuficiente | `400 + INSUFFICIENT_FUNDS` | `400 InvalidOperationException` |
| Cuenta inactiva | `400 + ACCOUNT_NOT_ACTIVE` | `400 InvalidOperationException` |
| Key duplicada en progreso | `409 + TRANSACTION_IN_PROGRESS` | `423 Locked` |
| Key reusada con distinto payload | `409 + IDEMPOTENCY_KEY_REUSED` | **`400 Idempotency key reused...`** |
| Key expirada | `409 + IDEMPOTENCY_KEY_EXPIRED` | Manejo implícito (reintento permitido tras FAILED) |
| Formato de respuesta | Estandarizado `{ success, code, description, data }` | DTOs tipados directos |

### Integración con el resto del sistema

| Aspecto | AccountService | TransactionService |
|---|---|---|
| Rate limiting | ✅ `[EnableRateLimiting("financial")]` | ✅ `[EnableRateLimiting("financial")]` |
| Route versioning | ✅ `/api/v1/accounts` | ✅ `/api/v1/transactions` |
| X-Correlation-ID en response | ❌ | ✅ |
| AuditLog en BD | ❌ | ✅ |
| IdempotencyService | ❌ (propio) | ✅ (`IIdempotencyService` inyectado) |
| Transacción Serializable | ✅ | ✅ |

---

## Veredicto Actualizado

### ✅ Lo que está listo para demo

| Checklist | Estado | Evidencia |
|---|---|---|
| Webhook funcional | ✅ | `WebhookNotifier` dispara POST tras commit |
| Un solo flujo visible en Swagger | ✅ | `TransactionsController` tiene `IgnoreApi` |
| Idempotencia atómica (doble click) | ✅ | Redis SET NX + replay exacto |
| Aislamiento tenant real | ✅ | FK compuestas + JWT claim + RLS |
| Multimoneda + comisión | ✅ | Transfer/Deposit/Withdraw con fee |
| Correlation-ID propagado | ✅ | Header → BD → Response header |
| CORS habilitado | ✅ | `AddCors()` + `UseCors()` |
| Enum FAILED en BD | ✅ | Script SQL + EF Core mapping |

### ⚠️ Deuda técnica conocida (no bloqueante para demo)

| Item | Impacto | Plan post-demo |
|---|---|---|
| Cache de tasas/config en TransactionService | Performance bajo carga | Migrar `IDistributedCache` de AccountService |
| Request hash usa key determinista, no body real | Seguridad idempotencia parcial | Hashear `Request.Body` en middleware |
| Error codes HTTP no estandarizados en TransactionService | Consistencia API | Wrapper `ServiceResponse` unificado |
| Serilog + correlation enricher | Observabilidad | Agregar paquetes + configuración |
| JWT Key en appsettings.json | Seguridad | Variable de entorno |

---

## Checklist de Correcciones — **COMPLETADO**

- [x] **Webhook**: HTTP POST a `Tenant.WebhookUrl` después de transfer/deposit/withdraw exitoso
- [x] **CORS**: `AddCors()` con política permisiva
- [x] **Route versioning**: `TransactionsController` → `/api/v1/transactions`
- [x] **Enum FAILED**: Agregado a `idempotency_state` (SQL + C# + EF Core)
- [x] **Request hash validation**: En `IdempotencyService.CompleteAsync` y `FailAsync`
- [x] **Rate limiting**: `[EnableRateLimiting("financial")]` en `TransactionsController`
- [x] **IdempotencyService robusto**: Persistencia dual Redis+DB, estados PROCESSING/COMPLETED/FAILED
- [x] **TransactionService completo**: Deposit/Withdraw/Transfer con audit, commission, webhook, correlation-id
- [x] **Swagger limpio**: `TransactionsController` oculto (`IgnoreApi = true`)
- [x] **IdempotencyRecord entity**: Creada en Domain + DbSet en BankOsDbContext + configuración completa

---

## Próximos Pasos Post-Demo (Prioridad)

1. **Cache distribuido** — Migrar `IDistributedCache` a `TransactionService` para tasas y config tenant
2. **Request hash real** — Middleware que hashee `Request.Body` y pase al service via `HttpContext.Items`
3. **Response wrapper unificado** — `ServiceResponse` para ambos servicios
4. **Serilog** — Con `UseSerilogRequestLogging` + correlation ID enricher
5. **JWT Key** — Leer de `EnvironmentVariable` / Key Vault
6. **Migración FAILED** — Generar migration EF Core para aplicar `ALTER TYPE` en BD fresca