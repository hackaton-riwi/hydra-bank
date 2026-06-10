# Hydra Bank API

API bancaria multi-tenant desarrollada en ASP.NET Core con JWT, Identity, Entity Framework Core y PostgreSQL.

El sistema separa la administración del tenant de la operación bancaria del cliente. Un `ADMIN` configura el tenant y sus tasas, pero la creación y operación de cuentas bancarias pertenece al `CLIENT` autenticado.

## Reglas implementadas

- API versionada bajo `/api/v1/...`.
- Las operaciones financieras mutables requieren `Idempotency-Key` con formato UUID.
- La creación de cuenta solo la puede hacer un usuario con rol `CLIENT`.
- El titular de una cuenta se toma del `user_id` del JWT, no del body.
- Las cuentas tienen número único, titular, saldo, moneda, estado y fecha de creación.
- La desactivación de cuentas usa soft delete: cambia estado a `INACTIVE` y conserva el registro.
- No se permiten depósitos, retiros ni transferencias sobre cuentas inactivas o bloqueadas.
- Depósitos y retiros validan monto positivo, cuenta activa y saldo suficiente cuando aplica.
- Transferencias internas validan tenant, cuentas activas, saldo suficiente, comisión y conversión multimoneda.
- Las tasas de cambio son estáticas y configuradas por tenant.
- El historial de transacciones es paginado y permite filtros por fecha y tipo.
- Redis se usa como caché distribuida para configuración de tenant y tasas de cambio.

## Redis y caché

Redis está conectado desde [Program.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Program.cs>) con `AddStackExchangeRedisCache`.

Configuración:

- [Hydra.Api/appsettings.json](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/appsettings.json>): `ConnectionStrings:Redis`.
- [Hydra.Api/appsettings.Development.json](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/appsettings.Development.json>): `ConnectionStrings:Redis`.
- Si la cadena `Redis` está vacía, la API usa `AddDistributedMemoryCache` como fallback local.

Levantar Redis local:

```bash
docker compose up -d redis
```

Uso actual del caché:

- [BankCacheKeys.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Application/Caching/BankCacheKeys.cs>) centraliza las llaves.
- [AccountService.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Application/Services/AccountService.cs>) cachea configuración de tenant con TTL de 10 minutos.
- [AccountService.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Application/Services/AccountService.cs>) cachea tasas `tenant + moneda origen + moneda destino` con TTL de 30 minutos.
- [ExchangeRatesController.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Controllers/ExchangeRatesController.cs>) invalida la tasa cacheada cuando el `ADMIN` guarda o actualiza una tasa.

Llaves usadas:

```text
HydraBank:bankos:tenant:{tenantId}:config
HydraBank:bankos:tenant:{tenantId}:exchange:{fromCurrency}:{toCurrency}
```

La base de datos sigue siendo la fuente de verdad. Redis solo evita consultas repetidas en operaciones financieras.

## Idempotencia

La idempotencia aplica a todas las operaciones financieras mutables:

- `POST /api/v1/accounts/{accountId}/deposit`
- `POST /api/v1/accounts/{accountId}/withdraw`
- `POST /api/v1/accounts/transfer`

Dónde está implementada:

- [AccountsController.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Controllers/AccountsController.cs>) exige y valida el header `Idempotency-Key` como UUID.
- [AccountService.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Application/Services/AccountService.cs>) ejecuta `ExecuteIdempotentAsync`.
- [IdempotencyRecord.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Domain/Entities/IdempotencyRecord.cs>) modela la persistencia.
- [BankOsDbContext.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Infrastructure/DATA/BankOsDbContext.cs>) define índice único por `tenant_id + user_id + idempotency_key`.

Cómo funciona:

- Cada operación calcula un hash con tipo de operación, tenant, usuario, cuentas y monto.
- Se crea un registro en `idempotency_records` con estado `PROCESSING` y expiración de 24 horas.
- Si la operación termina, se guarda `COMPLETED`, `status_code` y `response_body`.
- Si llega la misma llave ya completada, se retorna el mismo status code y body guardado.
- Si llega la misma llave mientras está `PROCESSING`, se responde `409 Conflict` con `TRANSACTION_IN_PROGRESS`.
- Si la misma llave se reutiliza con otro request, se responde `409 Conflict` con `IDEMPOTENCY_KEY_REUSED`.
- La llave se ata al par `tenant_id + user_id`, así otro tenant o usuario no comparte idempotencia.

## Rate limiting

El rate limiting está configurado en [Program.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Program.cs>) con `AddRateLimiter`.

Políticas:

- `auth`: 5 peticiones por minuto.
- `financial`: 30 peticiones por minuto.
- Si el usuario está autenticado, la partición es `user:{userId}`.
- Si no está autenticado, la partición es `ip:{remoteIp}`.
- Cuando se supera el límite, responde `429 Too Many Requests`.

Dónde se aplica:

- [AuthController.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Controllers/AuthController.cs>): `[EnableRateLimiting("auth")]` en endpoints de autenticación.
- [AccountsController.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Controllers/AccountsController.cs>): `[EnableRateLimiting("financial")]` en cuentas, depósitos, retiros, transferencias e historial.
- [TenantsController.cs](</home/duvan/Documents/carpeta proyecto-hackaton/hydra-bank/Hydra.Api/Controllers/TenantsController.cs>): `[EnableRateLimiting("financial")]`.

## Headers obligatorios

Rutas protegidas:

```http
Authorization: Bearer TU_TOKEN
```

Operaciones financieras (`deposit`, `withdraw`, `transfer`):

```http
Idempotency-Key: 5f44a350-2e19-46e8-9fe7-40e6cf93e218
```

La idempotencia se asocia a `tenant_id + user_id + Idempotency-Key` y expira a las 24 horas. Si una clave ya terminó correctamente, la API retorna exactamente el mismo status code y body guardado. Si la misma clave está en proceso, retorna `409 Conflict`.

## Respuesta estándar

Flujos manejados por la lógica bancaria retornan una estructura uniforme:

```json
{
  "success": true,
  "code": "TRANSFER_SUCCESS",
  "description": "Transferencia realizada correctamente",
  "data": {}
}
```

Errores:

```json
{
  "success": false,
  "code": "INSUFFICIENT_FUNDS",
  "description": "Saldo insuficiente para cubrir el monto y la comisión"
}
```

## Flujo recomendado

### 1. Crear tenant

```http
POST /api/v1/tenants
Content-Type: application/json

{
  "nombreTenant": "Manhattan Trust Bank",
  "correo": "admin@manhattan.test"
}
```

La API genera internamente el `slug`, crea el usuario `ADMIN` del tenant, devuelve una contraseña temporal y asigna valores por defecto para moneda, límite y comisión.

### 2. Registrar cliente dentro del tenant

Este endpoint crea el usuario de autenticación y el usuario bancario con rol `CLIENT`.

```http
POST /api/v1/auth/register
Content-Type: application/json

{
  "tenantSlug": "manhattan-trust",
  "fullName": "Cliente Uno",
  "email": "cliente@manhattan.test",
  "password": "Client123"
}
```

La respuesta entrega un JWT con `tenant_id`, `user_id` y rol `CLIENT`.

### 3. Login

```http
POST /api/v1/auth/login
Content-Type: application/json

{
  "tenantSlug": "manhattan-trust",
  "email": "cliente@manhattan.test",
  "password": "Client123"
}
```

## Tasas de cambio

Las transferencias entre monedas distintas usan tasas estáticas por tenant. Las configura el `ADMIN` del tenant.

```http
POST /api/v1/exchange-rates
Authorization: Bearer TOKEN_ADMIN_TENANT
Content-Type: application/json

{
  "fromCurrency": "USD",
  "toCurrency": "COP",
  "rate": 3900.00
}
```

Consultar tasas:

```http
GET /api/v1/exchange-rates
Authorization: Bearer TOKEN_ADMIN_TENANT
```

## Cuentas bancarias

### Crear cuenta propia

Solo `CLIENT`. El backend ignora cualquier intento de asignar `ownerId`; el titular siempre es el `user_id` del token.

```http
POST /api/v1/accounts
Authorization: Bearer TOKEN_CLIENTE
Content-Type: application/json

{
  "currency": "USD"
}
```

Respuesta:

```json
{
  "success": true,
  "code": "ACCOUNT_CREATED",
  "description": "Cuenta creada correctamente",
  "data": {
    "id": "uuid",
    "accountNumber": "1234567890",
    "ownerId": "uuid",
    "balance": 0,
    "currency": "USD",
    "status": "ACTIVE",
    "createdAt": "2026-06-10T00:00:00Z"
  }
}
```

### Desactivar cuenta

Soft delete: no elimina el registro físico.

```http
DELETE /api/v1/accounts/{accountId}
Authorization: Bearer TOKEN_CLIENTE
```

## Operaciones financieras

### Depósito

```http
POST /api/v1/accounts/{accountId}/deposit
Authorization: Bearer TOKEN_CLIENTE
Idempotency-Key: 99999999-9999-4999-8999-999999999999
Content-Type: application/json

{
  "amount": 1000
}
```

Valida cuenta propia, cuenta activa, monto positivo y límite máximo del tenant.

### Retiro

```http
POST /api/v1/accounts/{accountId}/withdraw
Authorization: Bearer TOKEN_CLIENTE
Idempotency-Key: bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb
Content-Type: application/json

{
  "amount": 250
}
```

Valida cuenta propia, cuenta activa, monto positivo, límite del tenant y saldo suficiente.

### Transferencia interna

```http
POST /api/v1/accounts/transfer
Authorization: Bearer TOKEN_CLIENTE
Idempotency-Key: dddddddd-dddd-4ddd-8ddd-dddddddddddd
Content-Type: application/json

{
  "sourceAccountId": "uuid-cuenta-origen",
  "destinationAccountId": "uuid-cuenta-destino",
  "amount": 100
}
```

Reglas:

- La cuenta origen debe pertenecer al cliente autenticado.
- La cuenta destino debe existir en el mismo tenant.
- Ambas cuentas deben estar `ACTIVE`.
- El saldo origen debe cubrir `amount + fee`.
- Si las monedas son distintas, se usa `exchange_rates` del tenant.
- El débito ocurre en la moneda origen y el crédito en la moneda destino.

## Historial de transacciones

Paginación obligatoria por `limit` y `offset`.

```http
GET /api/v1/accounts/transactions?limit=20&offset=0&type=TRANSFER&from=2026-06-01T00:00:00Z&to=2026-06-30T23:59:59Z
Authorization: Bearer TOKEN_CLIENTE
```

Campos registrados por transacción:

- tipo de operación (`DEPOSIT`, `WITHDRAW`, `TRANSFER`)
- monto original
- monto convertido cuando aplica
- tasa de cambio aplicada
- comisión cobrada
- cuenta origen y destino
- fecha de creación
- estado (`SUCCESS` o `FAILED`)
- `Idempotency-Key`

## Estados de cuenta

- `ACTIVE`: permite operaciones financieras.
- `INACTIVE`: cuenta desactivada por soft delete.
- `BLOCKED`: cuenta bloqueada operativamente.

## Compilación

```bash
dotnet build Hydra.Api/Hydra.Api.csproj
```

## Notas de implementación

- Las operaciones financieras usan transacciones de base de datos con aislamiento `Serializable`.
- La tabla `idempotency_records` guarda el estado de procesamiento y el body/status original.
- Las respuestas de replay por idempotencia se devuelven desde la respuesta persistida, sin recalcular saldos.
- Las transferencias fallidas por cuenta inactiva, fondos insuficientes o tasa inexistente se registran en `transactions` con estado `FAILED`.
