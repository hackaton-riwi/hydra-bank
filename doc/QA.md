# Plan de Pruebas — BankOS

## Preparación

```bash
# Iniciar servicios
docker compose up -d

# Ejecutar migraciones
dotnet ef database update --project Hydra.Infrastructure --startup-project Hydra.Api

# Iniciar API
dotnet run --project Hydra.Api
```

---

## Escenario 1: Dos Tenants en Paralelo

```http
### Crear Tenant A
POST /api/v1/tenants
{ "nombreTenant": "Banco Alfa", "correo": "admin@bancoalfa.com" }

### Crear Tenant B
POST /api/v1/tenants
{ "nombreTenant": "Banco Beta", "correo": "admin@bancobeta.com" }

### Registrar en A
POST /api/v1/auth/register
{ "tenantSlug": "banco-alfa", "fullName": "Cliente Compartido",
  "documentNumber": "123456789", "email": "cliente@alfa.com", "password": "Cliente123" }

### Registrar en B (mismo documento)
POST /api/v1/auth/register
{ "tenantSlug": "banco-beta", "fullName": "Cliente Compartido",
  "documentNumber": "123456789", "email": "cliente@beta.com", "password": "Cliente123" }
```

**Resultado:** Cada token ve solo los datos de su tenant. Mismo documento, cuentas aisladas.

---

## Escenario 2: Idempotencia (Reintento)

```http
### Request 1
POST /api/v1/accounts/transfer
Authorization: Bearer <token>
Idempotency-Key: 11111111-1111-1111-1111-111111111111
X-Correlation-ID: 22222222-2222-2222-2222-222222222222
{ "destinationDocumentNumber": "999999999", "amount": 50000 }

### Request 2 (misma key)
POST /api/v1/accounts/transfer
Authorization: Bearer <token>
Idempotency-Key: 11111111-1111-1111-1111-111111111111
X-Correlation-ID: 22222222-2222-2222-2222-222222222222
{ "destinationDocumentNumber": "999999999", "amount": 50000 }
```

**Resultado:** Request 1 = `200` (debitado). Request 2 = `200` (replay, NO debitado).

```sql
SELECT COUNT(*) FROM transactions
WHERE idempotency_key = '11111111-1111-1111-1111-111111111111';
-- → 1
```

---

## Escenario 3: Multimoneda + Comisión

Setup: tenant con `fee_type=PERCENTAGE`, `fee_value=2`, tasa USD→COP=4000.

```http
POST /api/v1/accounts/transfer
Authorization: Bearer <token>
Idempotency-Key: 33333333-3333-3333-3333-333333333333
{ "destinationDocumentNumber": "999999999", "amount": 100 }
```

**Resultado:** Débito A = 102 USD, Crédito B = 400,000 COP, comisión = 2 USD.

---

## Pruebas Detalladas

### Prueba 1: Transferencia Normal

| Paso | Acción | Esperado |
|------|--------|----------|
| 1 | Registrar Tenant | 201 |
| 2 | Registrar Cliente A | 201 |
| 3 | Registrar Cliente B | 201 |
| 4 | Recargar cuenta A (500,000) | 200 |
| 5 | Transferir A→B (50,000) | 200 |
| 6 | Saldo A = 448,000, Saldo B = 50,000 | ✅ |

### Prueba 2: Misma Key Dos Veces

| Paso | Acción | Esperado |
|------|--------|----------|
| 1 | Transferir con key X | 200 |
| 2 | Misma key X (replay) | 200 mismo body |
| 3 | Saldo debitado 1 vez | ✅ |

### Prueba 3: Key Concurrente (doble click)

| Paso | Acción | Esperado |
|------|--------|----------|
| 1 | 2 requests simultáneos key X | 200 + 423 |
| 2 | Saldo debitado 1 vez | ✅ |

### Prueba 4: Fondos Insuficientes

| Estado inicial | Cuenta A = 100 |
|---|---|
| Transferir 500 | 400 Bad Request |
| Saldo A sin cambios | ✅ |
| Reintentar con fondos (misma key) | ✅ |

### Prueba 5: Conversión de Moneda

| Configuración | Cuenta A: USD, Cuenta B: COP, tasa: 1 USD = 4000 COP |
|---|---|
| Transferir 100 USD | A: -100 USD, B: +400,000 COP |

### Prueba 6: Comisión

| FeeType | Fórmula | Ejemplo |
|---------|---------|---------|
| FIXED | fee = fee_value | fee_value=5 → comisión=5 |
| PERCENTAGE | fee = amount × fee_value / 100 | 1000×2%=20 |

### Prueba 7: Serializable (carrera entre transfers distintas)

| Estado | Cuenta A = 1000 |
|--------|----------------|
| Transfer 1: A→B, 700 | Una gana 200 OK |
| Transfer 2: A→C, 700 | Otra falla 409 |
| Cuenta A nunca negativa | ✅ |

Son keys distintas, `423` NO aplica. `IsolationLevel.Serializable` fuerza `serialization_failure (40001)`.

### Prueba 8: Multi-Tenant

| Tenant A cuenta "123" | Tenant B cuenta "123" |
|---|---|
| Request A intentando acceder a B | 404 / 400 |
| FK compuestas bloquean cruce | ✅ |

### Prueba 9: Idempotencia Fail + Reintento

| Request 1 (key=retry-key, amount=9999, saldo=1000) | 400 (insuficiente) |
|---|---|
| Request 2 (key=retry-key, amount=500, saldo=1000) | 200 OK, A=500 |
| Lock liberado tras fail | ✅ |

> ⚠️ Deuda técnica: El reintento usa misma key con payload distinto. Aceptable para hackatón porque `FailAsync` no persiste `request_hash`.

---

## Resumen de Pruebas

| # | Escenario | HTTP esperado | Doble gasto |
|---|-----------|---------------|-------------|
| 1 | Transferencia normal | 200 | N/A |
| 2 | Misma key dos veces | 200 + 200 | No |
| 3 | Dos simultáneas misma key | 200 + 423 | No |
| 4 | Fondos insuficientes | 400 | No |
| 5 | Conversión de moneda | 200 | N/A |
| 6 | Comisión porcentual | 200 | N/A |
| 7 | Dos transfers distintas simultáneas | 200 + 409 | No |
| 8 | Cruce multi-tenant | 404/400 | N/A |
| 9 | Fail + reintento misma key | 400 + 200 | No |

---

## Escenarios de Estrés (Pre-Demo)

1. 10 transfers simultáneas con distintas keys sobre la misma cuenta
2. 10 transfers simultáneas con la **misma** key (1 procesa, 9 locked)
3. Apagar Redis → error controlado (no NullReferenceException)
4. Apagar PostgreSQL → error controlado, sin locks huérfanos

---

## Arquitectura de Idempotencia Verificada

```
Controller → TransactionService
                ├── IIdempotencyService → Redis (SET NX) + BD (persistencia)
                ├── BankOsDbContext     → PostgreSQL (Serializable)
                └── AuditLog           → misma transacción SQL
```

**Puntos clave:**
- `StartProcessingAsync`: `SET key value NX EX` (atómico, sin race condition)
- `CompleteAsync`: se ejecuta DESPUÉS de `CommitAsync`
- `bool committed`: evita `RollbackAsync` sobre transacción ya confirmada
- `AuditLog`: se guarda en el mismo `SaveChangesAsync`
- `FailAsync`: persiste registro `FAILED` en PostgreSQL + elimina lock de Redis
- `CompleteAsync`: upsert — si existe registro `FAILED`, lo actualiza a `COMPLETED`
- `TransactionInProgressException` → HTTP 423 Locked

---

## Verificación de Aislamiento Multi-Tenant

```sql
-- Cliente solo ve sus cuentas
SELECT * FROM accounts WHERE owner_id = '<uid>' AND tenant_id = '<tid>';

-- No hay cuentas de otro tenant
SELECT * FROM accounts WHERE tenant_id <> '<tid>'; -- vacío

-- FK compuestas impiden referencias cruzadas
INSERT INTO accounts (tenant_id, owner_id)
VALUES ('<tenant_A>', '<user_B>'); -- FK violation!
```

---

## Cobertura de Endpoints

| Método | Ruta | Auth | Idempotencia |
|--------|------|------|--------------|
| POST | /api/v1/auth/register | No | No |
| POST | /api/v1/auth/login | No | No |
| GET | /api/v1/auth/me | Sí | No |
| GET | /api/v1/tenants | No | No |
| POST | /api/v1/tenants | No | No |
| POST | /api/v1/accounts | Sí | No |
| DELETE | /api/v1/accounts/{id} | Sí | No |
| POST | /api/v1/accounts/recharge | Sí | No |
| POST | /api/v1/accounts/transfer | Sí | Sí |
| POST | /api/v1/accounts/deposit | Sí | Sí |
| POST | /api/v1/accounts/withdraw | Sí | Sí |
| GET | /api/v1/accounts/transactions | Sí | No |
