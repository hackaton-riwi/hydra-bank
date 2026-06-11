# Arquitectura del Sistema — BankOS

## Visión General

BankOS sigue una arquitectura limpia (Clean Architecture) con 4 proyectos .NET que separan responsabilidades en capas concéntricas. El sistema es multitenant, con aislamiento de datos a nivel de base de datos y aplicación.

## Diagrama de Capas

```
┌──────────────────────────────────────────────────────────────────────┐
│                        HYDRA.API (Presentación)                       │
│                                                                       │
│  ┌────────────────────────────────────────────────────────────────┐  │
│  │  Controllers                                                    │  │
│  │  ┌──────────────┐  ┌────────────────┐  ┌──────────────────┐   │  │
│  │  │AuthController │  │TenantsController│  │AccountsController│   │  │
│  │  │ /api/v1/auth  │  │ /api/v1/tenants │  │ /api/v1/accounts │   │  │
│  │  └──────┬───────┘  └───────┬────────┘  └───────┬──────────┘   │  │
│  │         │                  │                    │               │  │
│  │         │       ┌─────────┴──────────┐          │               │  │
│  │         │       │TransactionsController│        │               │  │
│  │         │       │(oculto en Swagger)   │        │               │  │
│  │         │       └─────────┬──────────┘          │               │  │
│  └───────────────────────────┼──────────────────────┘               │
│                              │                                      │
│  ┌───────────────────────────┴────────────────────────────────────┐ │
│  │  Middleware / Filtros / Rate Limiting / JWT Auth               │ │
│  └───────────────────────────┬────────────────────────────────────┘ │
└──────────────────────────────┼────────────────────────────────────────┘
                               │
┌──────────────────────────────┼────────────────────────────────────────┐
│            HYDRA.APPLICATION  │  (Aplicación)                         │
│                               ▼                                       │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │                       IAccountService                            │ │
│  │  ┌──────────────────────────────────┐   ┌──────────────────────┐ │ │
│  │  │       AccountService             │   │  TransactionService  │ │ │
│  │  │  - CreateAsync()                 │   │  - DepositAsync()    │ │ │
│  │  │  - DeactivateAsync()             │   │  - WithdrawAsync()   │ │ │
│  │  │  - RechargeAsync()               │   │  - TransferAsync()   │ │ │
│  │  │  - GetTransactionsAsync()        │   │                      │ │ │
│  │  │  - TransferAsync()               │   └────────┬─────────────┘ │ │
│  │  │  - DepositAsync()                │            │               │ │
│  │  │  - WithdrawAsync()               │            │               │ │
│  │  └──────────────────────────────────┘            ▼               │ │
│  │                                          ┌──────────────────┐    │ │
│  │                                          │IIdempotencyService│    │ │
│  │  ┌──────────────────────────────────┐    │  - StartProc()   │    │ │
│  │  │    IdempotencyService            │    │  - Complete()    │    │ │
│  │  │  - Redis SET NX (lock atómico)   │    │  - Fail()        │    │ │
│  │  │  - DB PostgreSQL (persistencia)   │    │  - GetAsync()    │    │ │
│  │  └────────┬─────────────────────────┘    └──────────────────┘    │ │
│  │           │                                                      │ │
│  │  ┌────────┴─────────────────────────┐    ┌──────────────────┐    │ │
│  │  │    WebhookNotifier               │    │IWebhookNotifier  │    │ │
│  │  │  - Fire-and-forget HTTP POST     │    └──────────────────┘    │ │
│  │  └──────────────────────────────────┘                            │ │
│  └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────┬────────────────────────────────────────┘
                               │
┌──────────────────────────────┼────────────────────────────────────────┐
│                HYDRA.DOMAIN  │  (Dominio)                              │
│                               ▼                                       │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  Entities:    Tenant  User  Account  Transaction                 │ │
│  │               AuditLog  IdempotencyRecord                        │ │
│  │                                                                  │ │
│  │  Enums:       UserRole  AccountStatus  TransactionType           │ │
│  │               TransactionStatus  FeeTypeEnum  IdempotencyState   │ │
│  │                                                                  │ │
│  │  Exceptions:  TransactionInProgressException                     │ │
│  └──────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────┬────────────────────────────────────────┘
                               │
┌──────────────────────────────┼────────────────────────────────────────┐
│       HYDRA.INFRASTRUCTURE   │  (Persistencia)                        │
│                               ▼                                       │
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │  BankOsDbContext (EF Core + Npgsql)                              │ │
│  │                                                                  │ │
│  │  DbSets:  Accounts, AuditLogs, Tenants, Transactions             │ │
│  │           IdempotencyRecords, BankUsers                          │ │
│  │                                                                  │ │
│  │  Configuración:                                                  │ │
│  │   - Enums PostgreSQL (6 tipos personalizados)                    │ │
│  │   - Check constraints (balance >= 0, monedas ISO, etc)          │ │
│  │   - FK compuestas tenant_id + id                                │ │
│  │   - Índices para performance                                    │ │
│  │   - Propiedades JSONB para valores old/new                      │ │
│  └──────────────────┬───────────────────────────────────────────────┘ │
└─────────────────────┼─────────────────────────────────────────────────┘
                      │
          ┌───────────┴───────────┐
          ▼                       ▼
   PostgreSQL 16             Redis 7
   - Enums personalizados    - SET NX idempotencia
   - RLS (Row Level Sec.)    - Cache distribuido
   - JSONB audit logs        - Expiración 24h
   - Check constraints
```

## Estrategia Multitenant

### Aislamiento a Nivel de Base de Datos

Todas las tablas sensibles contienen `tenant_id` como columna obligatoria:

```sql
users.tenant_id
accounts.tenant_id
transactions.tenant_id
audit_logs.tenant_id
idempotency_records.tenant_id
```

**FK compuestas** impiden cruces accidentales entre tenants:

```
FOREIGN KEY (tenant_id, owner_id) REFERENCES users(tenant_id, id)
FOREIGN KEY (tenant_id, source_account_id) REFERENCES accounts(tenant_id, id)
FOREIGN KEY (tenant_id, destination_account_id) REFERENCES accounts(tenant_id, id)
```

**Row Level Security (RLS)** activado en tablas críticas como segunda capa de defensa.

### Aislamiento a Nivel de Aplicación

1. **JWT Claims**: El token incluye `tenant_id`, `user_id`, `tenant_role` como claims.
2. **Extracción automática**: Los servicios extraen `(tenantId, userId)` del `IHttpContextAccessor`.
3. **Todas las queries** filtran por `x.TenantId == tenantId && x.OwnerId == userId`.
4. **Cuentas destino**: Se buscan siempre dentro del mismo tenant.

## Flujo de Operaciones Financieras

### Transferencia (end-to-end)

```
Cliente                    API                          Servicios                BD + Redis
  │                         │                              │                      │
  │ POST /api/v1/accounts/ │                              │                      │
  │ transfer               │                              │                      │
  │ Idempotency-Key: X     │                              │                      │
  │ X-Correlation-ID: Y    │                              │                      │
  │────────────────────────>│                              │                      │
  │                         │                              │                      │
  │                         │  AccountService.Transfer()   │                      │
  │                         │─────────────────────────────>│                      │
  │                         │                              │                      │
  │                         │  Extrae tenantId, userId     │                      │
  │                         │  del JWT claims              │                      │
  │                         │                              │                      │
  │                         │  Validation:                 │                      │
  │                         │  - Headers presentes         │                      │
  │                         │  - Dto válido                │                      │
  │                         │                              │                      │
  │                         │  TransactionService          │                      │
  │                         │  .TransferAsync()            │                      │
  │                         │─────────────────────────────>│                      │
  │                         │                              │                      │
  │                         │                              │  SET NX processing   │
  │                         │                              │──────────────────────>│
  │                         │                              │<────── acquired ─────│
  │                         │                              │                      │
  │                         │                              │  BEGIN Serializable  │
  │                         │                              │──────────────────────>│
  │                         │                              │                      │
  │                         │                              │  Validaciones:       │
  │                         │                              │  - Monto > 0         │
  │                         │                              │  - Tenant existe     │
  │                         │                              │  - Cuenta origen     │
  │                         │                              │  - Cuenta destino     │
  │                         │                              │    (busca por doc)   │
  │                         │                              │  - Ambas activas     │
  │                         │                              │  - Límite tenant     │
  │                         │                              │  - Saldo suficiente  │
  │                         │                              │                      │
  │                         │                              │  CalculateFee()      │
  │                         │                              │  - FIXED / PERCENTAGE│
  │                         │                              │                      │
  │                         │                              │  Débito origen      │
  │                         │                              │──────────────────────>│
  │                         │                              │  Crédito destino    │
  │                         │                              │──────────────────────>│
  │                         │                              │                      │
  │                         │                              │  INSERT transaction  │
  │                         │                              │──────────────────────>│
  │                         │                              │  INSERT audit_log   │
  │                         │                              │──────────────────────>│
  │                         │                              │  COMMIT              │
  │                         │                              │──────────────────────>│
  │                         │                              │                      │
  │                         │                              │  CompleteAsync()    │
  │                         │                              │  - DB upsert        │
  │                         │                              │  - Redis set resp.  │
  │                         │                              │  - Delete lock      │
  │                         │                              │──────────────────────>│
  │                         │                              │                      │
  │                         │                              │  Webhook (fire&     │
  │                         │                              │  forget)            │
  │                         │                              │  HTTP POST →        │
  │                         │                              │  tenant.webhook_url │
  │                         │                              │                      │
  │                         │<─────────────────────────────│  TransferResponseDto │
  │<────────────────────────│                              │                      │
  │ 200 OK + response       │                              │                      │
  │ X-Correlation-ID: Y     │                              │                      │
```

## Sistema de Idempotencia

### Flujo de Lock Atómico

```
Request 1 (key: X)           Request 2 (key: X, simultáneo)
         │                            │
         │ SET NX processing:X        │ SET NX processing:X
         │ (Redis)                    │ (Redis)
         │                            │
         ├── acquired: true ──────────┤── acquired: false
         │                            │
         │ Process transaction        │ Check GET response:X
         │                            │
         │                            ├── exists? → replay respuesta
         │                            │
         │                            └── no? → 423 Locked
         │
         ├── CompleteAsync()
         │   ├── DB: INSERT/UPDATE
         │   └── Redis: SET response:X
         │                DEL processing:X
         │
         └── Return 200 OK
```

### Estados

| Estado | Descripción | Acción en reintento |
|--------|-------------|-------------------|
| PROCESSING | Operación en curso | 423 Locked (o esperar) |
| COMPLETED | Operación exitosa | Replay exacto de respuesta |
| FAILED | Operación fallida | Permitir reintento (nuevo lock) |

## Manejo de Errores

### Códigos de Error Internos

| Código HTTP | Escenario |
|-------------|-----------|
| 200 | Operación exitosa |
| 201 | Recurso creado |
| 400 | Error de validación (monto, saldo, cuenta inactiva) |
| 401 | Token inválido o expirado |
| 403 | Sin permisos (rol incorrecto) |
| 404 | Recurso no encontrado (tenant, cuenta) |
| 409 | Conflicto (email duplicado, idempotency key reusada) |
| 423 | Transacción en progreso (lock) |
| 429 | Rate limit excedido |

### Estructura de Respuesta

```json
{
  "success": true,
  "code": "TRANSFER_COMPLETED",
  "description": "Transferencia realizada correctamente",
  "data": { ... }
}
```

## Rate Limiting

| Policy | Endpoints | Límite | Ventana |
|--------|-----------|--------|---------|
| `auth` | `/api/v1/auth/*` | 5 requests | 1 minuto |
| `financial` | `/api/v1/accounts/*`, `/api/v1/transactions/*` | 30 requests | 1 minuto |

La partición se hace por `userId` (autenticado) o `IP` (anónimo).

## Webhooks

El sistema dispara notificaciones POST asíncronas al `WebhookUrl` configurado en cada tenant después de:

- Transferencia exitosa
- Depósito exitoso
- Retiro exitoso

Payload del webhook:

```json
{
  "event": "TRANSACTION_COMPLETED",
  "transactionId": "guid",
  "tenantId": "guid",
  "userId": "guid",
  "transactionType": "TRANSFER",
  "amount": 100000.00,
  "feeAmount": 2000.00,
  "status": "SUCCESS",
  "createdAt": "2026-06-10T12:00:00Z",
  "sourceAccountId": "guid",
  "destinationAccountId": "guid",
  "correlationId": "guid"
}
```

## Seguridad

1. **JWT HMAC-SHA256** con claims `tenant_id`, `user_id`, `tenant_role`
2. **Contraseñas hasheadas** con `PasswordHasher<TUser>` (bcrypt/scrypt)
3. **Identity dual**: Tabla `users` (datos bancarios) + `AspNetUsers` (autenticación)
4. **RLS** en PostgreSQL — segunda capa de defensa
5. **Rate limiting** por IP y userId
6. **Validaciones en BD** — check constraints evitan datos inválidos
7. **FK compuestas** impiden cruce entre tenants incluso en errores de código
