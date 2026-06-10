# Plan de Pruebas - Transacciones con Idempotencia

## Prueba 1 — Transferencia normal

**Estado inicial:**

| Cuenta | Saldo |
|---|---|
| A | 1000 |
| B | 500 |

**Request:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 200
}
```

**Esperado:**

| Cuenta | Saldo |
|---|---|
| A | 800 |
| B | 700 |

```
Response: 200 OK
{
  "status": "SUCCESS",
  "originalAmount": 200,
  "feeAmount": 0
}
```

**Verificar en BD:**
- `transactions` tiene 1 registro con `type = TRANSFER`, `status = SUCCESS`.
- `audit_logs` tiene 1 registro con `action = TRANSFER`.

---

## Prueba 2 — Mismo Idempotency-Key (dos veces)

**Estado inicial:**

| Saldo A | Saldo B |
|---|---|
| 1000 | 500 |

**Request 1:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 200,
  "idempotencyKey": "11111111-1111-1111-1111-111111111111"
}
```

**Request 2 (idéntico):**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 200,
  "idempotencyKey": "11111111-1111-1111-1111-111111111111"
}
```

**Esperado:**

```
Request 1: 200 OK (procesa, descuenta)
Request 2: 200 OK (respueta cacheada, NO descuenta)
```

**Verificar en BD:**
- Cuenta A = 800 (descontado UNA sola vez).
- Cuenta B = 700.
- `transactions` tiene 1 solo registro.

---

## Prueba 3 — Dos requests simultáneas con misma key (doble click)

**Estado inicial:**

| Saldo A | Saldo B |
|---|---|
| 1000 | 500 |

**Enviar simultáneamente:**

```
Request A: key = "aaaa"
Request B: key = "aaaa"
```

**Esperado:**

```
Request A: 200 OK (gana el lock, procesa)
Request B: 423 Locked (encuentra PROCESSING en Redis)
```

**Verificar en BD:**
- Saldo descontado una sola vez.
- 1 solo registro en `transactions`.

---

## Prueba 4 — Fondos insuficientes

**Estado inicial:**

| Cuenta A |
|---|
| 100 |

**Request:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 500
}
```

**Esperado:**

```
Response: 400 Bad Request
{
  "code": "INSUFFICIENT_FUNDS",
  "message": "Saldo insuficiente"
}
```

> También válido: 422 Unprocessable Entity. La validación de negocio NUNCA debe ser 500.

**Verificar en BD:**
- Cuenta A sigue en 100.
- No hay registros en `transactions`.
- No hay registros en `audit_logs`.
- El lock de idempotencia se liberó (se puede reintentar con la misma key).

---

## Prueba 5 — Conversión de moneda

**Configuración:**
- Cuenta A: USD
- Cuenta B: COP
- Tasa: 1 USD = 4000 COP

**Request:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 100
}
```

**Esperado:**

```
Cuenta A: -100 USD
Cuenta B: +400000 COP

Response:
{
  "convertedAmount": 400000,
  "exchangeRate": 4000,
  "feeAmount": 0
}
```

**Verificar en BD:**
- `converted_amount` = 400000.
- `exchange_rate` = 4000.

---

## Prueba 6 — Comisión

**Configuración del tenant:**
- `fee_type` = PERCENTAGE
- `fee_value` = 2

**Request:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 1000
}
```

**Esperado:**

```
Cuenta A: -1020 (1000 + 20 de comisión)
Cuenta B: +1000

Response:
{
  "feeAmount": 20
}
```

**Verificar en BD:**
- `fee_amount` = 20 en `transactions`.

| FeeType | Fórmula | Ejemplo |
|---|---|---|
| FIXED | fee = fee_value | fee_value = 5 → comisión = 5 |
| PERCENTAGE | fee = amount * fee_value / 100 | amount=1000, fee_value=2 → comisión = 20 |

---

## Prueba 7 — Serializable (carrera entre dos transfers distintas)

**Estado inicial:**

| Cuenta A |
|---|
| 1000 |

**Enviar simultáneamente:**

```
Transfer 1: A → B, amount 700
Transfer 2: A → C, amount 700
```

**Esperado:**

```
Una gana:  200 OK, A = 300
Otra falla: 409 Conflict (serialization_failure), A sigue en 1000
```

> Son keys distintas, por tanto 423 Locked NO aplica. Serializable fuerza una `serialization_failure (40001)` en PostgreSQL que debe capturarse como 409 Conflict.

**Verificar:**
- Cuenta A NUNCA queda negativa.
- PostgreSQL `IsolationLevel.Serializable` fuerza una `serialization_failure (40001)` si ambas compiten.

---

## Prueba 8 — Multi-tenant

**Configuración:**
- Tenant A tiene cuenta `123`
- Tenant B tiene cuenta `123`

**Request desde Tenant A:**

```
POST /api/transactions/transfer
{
  "sourceAccountId": "123",
  "destinationAccountId": "...",
  "amount": 100
}
```

intentando acceder a cuenta del Tenant B.

**Esperado:**

```
404 Not Found o 500 "Cuenta origen no encontrada"
```

**Verificar:**
- El `tenant_id` en el WHERE impide cruce.
- FK compuestas en BD bloquean cualquier referencia cruzada.

---

## Prueba 9 — Idempotencia con fail y reintento

**Estado inicial:**

| Cuenta A |
|---|
| 1000 |

**Request 1 (falla por fondos insuficientes):**

```
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 9999,
  "idempotencyKey": "retry-key"
}
```

Resultado: falla, Redis lock liberado.

**Request 2 (reintento con fondos suficientes):**

```
{
  "sourceAccountId": "A",
  "destinationAccountId": "B",
  "amount": 500,
  "idempotencyKey": "retry-key"
}
```

**Esperado:**

```
Request 1: 400 Bad Request (saldo insuficiente)
Request 2: 200 OK, A = 500, B = 500
```

**Verificar:**
- El lock se liberó después del fallo.
- El reintento con la misma key funciona.
- 1 sola transacción exitosa.

> ⚠️ **Deuda técnica:** El reintento usa la misma key con payload distinto (9999 → 500). Esto es aceptable para la hackatón porque `FailAsync` no persiste el `request_hash`. En producción, la misma key debe representar exactamente la misma operación; un payload diferente debería responder 409 Conflict.

---

## Resumen de resultados esperados

| # | Escenario | HTTP esperado | Doble gasto |
|---|---|---|---|
| 1 | Transferencia normal | 200 | N/A |
| 2 | Misma key dos veces | 200 + 200 | No |
| 3 | Dos requests simultáneas misma key | 200 + 423 | No |
| 4 | Fondos insuficientes | 400 | No |
| 5 | Conversión de moneda | 200 | N/A |
| 6 | Comisión porcentual | 200 | N/A |
| 7 | Dos transfers simultáneas distinta key | 200 + 409 | No |
| 8 | Cruce multi-tenant | 404 | N/A |
| 9 | Fail + reintento misma key | 400 + 200 | No |

---

## Escenarios de estrés recomendados antes de la demo

1. Lanzar 10 transfers simultáneas desde Postman Runner con distintas keys sobre la misma cuenta.
2. Lanzar 10 transfers simultáneas con la MISMA key.
3. Apagar Redis y verificar que el sistema responde con error controlado (no NullReferenceException).
4. Apagar PostgreSQL y verificar que Redis no queda con locks huérfanos.

---

## Arquitectura verificada

```
Controller (TransactionsController)
    │
    ▼
TransactionService
    │
    ├── IIdempotencyService  → Redis (SET NX) + BD (persistencia)
    │
    ├── BankOsDbContext       → PostgreSQL (Serializable)
    │
    └── AuditLog             → misma transacción SQL
```

**Puntos clave:**
- `StartProcessingAsync` usa `SET key value NX EX` (atómico, sin race condition).
- `CompleteAsync` se ejecuta DESPUÉS de `CommitAsync`.
- `bool committed` evita `RollbackAsync` sobre transacción ya confirmada.
- `AuditLog` se guarda en el mismo `SaveChangesAsync`.
- `FailAsync` persiste registro `FAILED` en PostgreSQL + elimina lock de Redis.
- `CompleteAsync` hace upsert: si ya existe un registro `FAILED`, lo actualiza a `COMPLETED`.
- `TransactionInProgressException` → HTTP 423 Locked.

**⚠️ Deuda técnica conocida:**

| Problema | Impacto | Fix real |
|---|---|---|
| `StartProcessingAsync` solo escribe en Redis, no en PostgreSQL. Si Redis cae entre el lock y el commit, el reintento no detecta el estado `PROCESSING`. | El `UNIQUE` en `(tenant_id, user_id, idempotency_key)` evita doble gasto, pero el usuario recibe 500 en lugar de respuesta cacheada. | Insertar registro `PROCESSING` en PostgreSQL al momento de `StartProcessingAsync`. |
| `FailAsync` no valida `request_hash`. | Reintentar con mismo `Idempotency-Key` pero payload distinto es aceptado. | Persistir y validar `request_hash` en `FailAsync`/`CompleteAsync`. |
