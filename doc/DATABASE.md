# Base de Datos — BankOS

## 1. Objetivo

La base de datos de BankOS fue diseñada para soportar un sistema bancario multitenant, donde varias instituciones financieras pueden operar dentro de la misma plataforma sin compartir ni exponer información entre ellas.

**Objetivos de diseño:**
- Aislamiento estricto entre tenants
- Integridad financiera
- Trazabilidad completa de operaciones
- Soporte para múltiples monedas
- Control de idempotencia en operaciones críticas
- Auditoría inmutable
- Compatibilidad con EF Core mediante claves primarias simples

---

## 2. Modelo Multitenant

La tabla central del sistema es `tenants`. Cada tenant representa una institución financiera independiente (banco, cooperativa, fintech).

**Configuración por institución:**
- `name` — nombre del tenant
- `slug` — identificador único para login/routing
- `main_currency` — moneda principal (COP, USD, EUR)
- `max_transaction_amount` — monto máximo por transacción
- `fee_type` / `fee_value` — comisión fija o porcentual
- `webhook_url` — endpoint de notificaciones externas

Cada institución tiene sus propias reglas de negocio. Un tenant puede operar en COP con comisión fija, mientras otro opera en USD con comisión porcentual.

---

## 3. Esquema General

```
tenants (1)
  │
  ├── users (N) ─── accounts (N)
  │                    │
  │                    ├── transactions (N) ──┐
  │                    │                       │
  │                    └── audit_logs (N)      │
  │                                            │
  └── idempotency_records (N) ────────────────┘
```

---

## 4. Aislamiento Físico por Tenant

Todas las tablas sensibles incluyen `tenant_id`:

```
users.tenant_id
accounts.tenant_id
transactions.tenant_id
audit_logs.tenant_id
idempotency_records.tenant_id
```

**FK compuestas** para evitar cruces accidentales:

```sql
FOREIGN KEY (tenant_id, owner_id) REFERENCES users(tenant_id, id)
```

Una cuenta del tenant A no puede pertenecer a un usuario del tenant B. Aunque ambos IDs existan, la BD rechaza la relación si el tenant_id no coincide. Esto es más fuerte que validar solo en backend.

Además, se agregaron `UNIQUE (tenant_id, id)` en todas las tablas para permitir FK compuestas sin cambiar las PK simples que EF Core espera.

---

## 5. Tablas

### tenants

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| name | VARCHAR(100) | Nombre del tenant |
| slug | VARCHAR(50) UNIQUE | Slug para login/routing |
| main_currency | VARCHAR(3) | Moneda principal |
| max_transaction_amount | DECIMAL(18,2) | Límite máximo por transacción |
| fee_type | fee_type_enum | FIXED o PERCENTAGE |
| fee_value | DECIMAL(18,4) | Valor de la comisión |
| webhook_url | TEXT | Endpoint de notificación |
| created_at | TIMESTAMPTZ | Fecha creación |
| updated_at | TIMESTAMPTZ | Fecha actualización |

**Check constraints:**
- `chk_fee_value`: fee_value >= 0
- `chk_max_amount`: max_transaction_amount > 0
- `chk_percentage_limit`: fee_type != 'PERCENTAGE' OR fee_value <= 100
- `chk_tenant_main_currency`: ~ '^[A-Z]{3}$'

---

### users

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| tenant_id | UUID FK → tenants | Tenant al que pertenece |
| full_name | VARCHAR(150) | Nombre completo |
| document_number | VARCHAR(64) | Número de documento |
| email | VARCHAR(150) | Correo electrónico |
| password_hash | TEXT | Hash de contraseña (bcrypt) |
| role | user_role | ADMIN o CLIENT |
| created_at | TIMESTAMPTZ | Fecha creación |
| updated_at | TIMESTAMPTZ | Fecha actualización |

**Constraints:**
- UNIQUE (tenant_id, document_number)
- UNIQUE (tenant_id, id)

**Roles:**
- `ADMIN` — administrar usuarios, cuentas, configuración, auditoría
- `CLIENT` — operar y consultar sus propias cuentas

---

### accounts

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| tenant_id | UUID FK | Tenant propietario |
| owner_id | UUID FK → users | Titular de la cuenta |
| account_number | VARCHAR(30) | Número de cuenta |
| balance | DECIMAL(18,2) | Saldo actual |
| currency | VARCHAR(3) | Moneda de la cuenta |
| status | account_status | ACTIVE, INACTIVE, BLOCKED |
| created_at | TIMESTAMPTZ | Fecha creación |
| updated_at | TIMESTAMPTZ | Fecha actualización |
| deactivated_at | TIMESTAMPTZ | Fecha desactivación lógica |

**Check constraints:**
- `chk_account_balance_positive`: balance >= 0
- `chk_account_currency`: ~ '^[A-Z]{3}$'

**FK compuesta:** (tenant_id, owner_id) → users(tenant_id, id)

No se eliminan físicamente las cuentas. En banca, borrar una cuenta rompería la trazabilidad histórica. Por eso se usa soft delete: la cuenta cambia a estado `INACTIVE` o `BLOCKED`, pero el registro permanece.

---

### transactions

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| tenant_id | UUID FK | Tenant |
| user_id | UUID FK → users | Usuario que ejecutó |
| type | transaction_type | DEPOSIT, WITHDRAW, TRANSFER |
| source_account_id | UUID NULL | Cuenta origen |
| destination_account_id | UUID NULL | Cuenta destino |
| original_amount | DECIMAL(18,2) | Monto solicitado |
| fee_amount | DECIMAL(18,2) DEFAULT 0 | Comisión cobrada |
| status | transaction_status | PENDING, SUCCESS, FAILED |
| correlation_id | UUID | ID de trazabilidad |
| created_at | TIMESTAMPTZ | Fecha |

**Check constraint — valida la estructura de cada operación:**

```sql
chk_transaction_account_shape:
  (type = 'DEPOSIT'    AND source IS NULL      AND dest IS NOT NULL)
  OR (type = 'WITHDRAW' AND source IS NOT NULL  AND dest IS NULL)
  OR (type = 'TRANSFER'  AND source IS NOT NULL AND dest IS NOT NULL AND source <> dest)
```

Cada tipo de operación tiene reglas estructurales distintas. La BD impide guardar transacciones incoherentes: un retiro sin cuenta origen o una transferencia hacia la misma cuenta.

---

### audit_logs

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| tenant_id | UUID FK | Tenant |
| user_id | UUID FK → users | Usuario que realizó la acción |
| action | VARCHAR(100) | Tipo de acción |
| old_value | JSONB | Estado anterior (before snapshot) |
| new_value | JSONB | Estado nuevo (after snapshot) |
| created_at | TIMESTAMPTZ | Fecha |

**Trigger `prevent_audit_mutation()`:** Impide UPDATE y DELETE sobre `audit_logs`. La auditoría debe ser inmutable.

---

### idempotency_records

| Columna | Tipo | Descripción |
|---------|------|-------------|
| id | UUID PK | Identificador único |
| tenant_id | UUID FK | Tenant |
| user_id | UUID FK → users | Usuario |
| idempotency_key | UUID | Clave de idempotencia |
| request_hash | TEXT | SHA256 del request |
| response_body | JSONB | Respuesta original para replay |
| status_code | INT | Código HTTP original |
| state | idempotency_state | PROCESSING, COMPLETED, FAILED |
| created_at | TIMESTAMPTZ | Fecha creación |
| expires_at | TIMESTAMPTZ | Fecha expiración (24h) |

**Constraints:**
- UNIQUE (tenant_id, user_id, idempotency_key)
- `chk_idempotency_expiration`: expires_at > created_at

Si una transferencia falla por red y el usuario reintenta, no debe cobrarse dos veces. Con esta tabla:
- Si la operación ya terminó → se devuelve la misma respuesta
- Si está en proceso → se responde conflicto (423)
- Si es nueva → se procesa una sola vez

---

## 6. Enums de PostgreSQL

| Enum | Valores |
|------|---------|
| account_status | `ACTIVE`, `INACTIVE`, `BLOCKED` |
| fee_type_enum | `FIXED`, `PERCENTAGE` |
| idempotency_state | `PROCESSING`, `COMPLETED`, `FAILED` |
| transaction_status | `PENDING`, `SUCCESS`, `FAILED` |
| transaction_type | `DEPOSIT`, `WITHDRAW`, `TRANSFER` |
| user_role | `ADMIN`, `CLIENT` |

---

## 7. Row Level Security (RLS)

RLS está activado en tablas críticas como segunda capa de defensa. Las políticas usan variables de sesión que el backend configura en cada request autenticado:

```sql
SELECT set_config('app.tenant_id', '<tenant_id>', true);
SELECT set_config('app.user_id', '<user_id>', true);
SELECT set_config('app.role', '<role>', true);
```

Ejemplos:
- Un cliente solo ve sus cuentas
- Un admin ve los datos de su tenant
- Nadie ve datos de otro tenant

Aunque un query del backend tenga un error, la BD filtra las filas permitidas según tenant, usuario y rol.

---

## 8. Índices

| Tabla | Índice | Propósito |
|-------|--------|-----------|
| tenants | UNIQUE (slug) | Login rápido por slug |
| users | UNIQUE (tenant_id, document_number) | Evitar documentos duplicados por tenant |
| accounts | UNIQUE (tenant_id, account_number) | Búsqueda rápida por número de cuenta |
| accounts | UNIQUE (tenant_id, owner_id) | Una cuenta por cliente |
| transactions | (tenant_id, created_at DESC) | Historial paginado por tenant |
| transactions | (correlation_id) | Trazabilidad por correlation ID |
| transactions | (type) | Filtro por tipo de operación |
| transactions | (source_account_id) | Consulta de transacciones origen |
| transactions | (destination_account_id) | Consulta de transacciones destino |
| idempotency_records | UNIQUE (tenant_id, user_id, idempotency_key) | Idempotencia |
| idempotency_records | (expires_at) | Limpieza de registros expirados |
| audit_logs | (tenant_id, created_at DESC) | Consulta de auditoría |

---

## 9. Decisiones de Compatibilidad con EF Core

Aunque el modelo usa aislamiento por `tenant_id`, se mantuvieron **claves primarias simples** (`id UUID PRIMARY KEY`) y se agregaron **claves únicas compuestas** (`UNIQUE (tenant_id, id)`).

**¿Por qué?** EF Core trabaja más fácilmente con PK simples, pero para proteger el modelo multitenant se necesitan referencias compuestas. Esta combinación permite compatibilidad con EF Core + integridad fuerte por tenant.

---

## 10. Resumen de Seguridad

1. `tenant_id` en todas las tablas sensibles
2. Foreign keys compuestas impiden cruces entre tenants
3. RLS filtra datos por sesión autenticada
4. Triggers para auditoría inmutable
5. Check constraints validan montos, monedas y estructura de transacciones
6. Idempotencia evita doble cobro
7. Soft delete preserva historial financiero
