# hydra-bank

Academic project repository for Manhattan Trust Bank (ID: 194).

## Arquitectura del Sistema

```
┌─────────────────────────────────────────────────────────────────┐
│                    HYDRA.API (Presentación)                      │
│  ┌──────────────────┐         ┌──────────────────────────────┐   │
│  │ AuthController   │         │  TenantsController           │   │
│  │ - Register       │         │  - Create Tenant             │   │
│  │ - Login          │         │  - Get Tenants (admin only)  │   │
│  └──────────────────┘         └──────────────────────────────┘   │
└────────────────┬─────────────────────────────────────────────────┘
                 │
                 │ Swagger/OpenAPI, JWT Authentication
                 │
┌────────────────┴─────────────────────────────────────────────────┐
│            HYDRA.APPLICATION (Aplicación)                        │
│  ┌─────────────────────────────────────────────────────────┐    │
│  │ DTOs                                                     │    │
│  │ - RegisterDto, LoginDto                                 │    │
│  │ - CreateTenantDto, AssignRoleDto                        │    │
│  └─────────────────────────────────────────────────────────┘    │
└────────────────┬─────────────────────────────────────────────────┘
                 │
┌────────────────┴─────────────────────────────────────────────────┐
│              HYDRA.DOMAIN (Lógica de Negocio)                    │
│  Entities:                                                        │
│  - Tenant: Configuración y datos del cliente                     │
│  - User: Usuarios dentro de un tenant (Admin/Client)             │
│  - Account: Cuentas bancarias (Balance, Currency)                │
│  - Transaction: Operaciones (Deposit, Withdraw, Transfer)        │
│  - ExchangeRate: Tasas de cambio por tenant                      │
│  - AuditLog: Registro de cambios (JSONB)                         │
│  - IdempotencyRecord: Prevenir duplicados en transacciones       │
│                                                                   │
│  Enums:                                                          │
│  - UserRole: ADMIN, CLIENT                                       │
│  - AccountStatus: ACTIVE, INACTIVE, BLOCKED                      │
│  - TransactionType: DEPOSIT, WITHDRAW, TRANSFER                  │
│  - TransactionStatus: PENDING, SUCCESS, FAILED                   │
│  - FeeTypeEnum: FIXED, PERCENTAGE                                │
└────────────────┬─────────────────────────────────────────────────┘
                 │
┌────────────────┴─────────────────────────────────────────────────┐
│         HYDRA.INFRASTRUCTURE (Persistencia)                      │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ BankOsDbContext (Entity Framework Core)                  │    │
│  │ - DbSet para todas las entidades                         │    │
│  │ - Configuración de constraints (PK, FK, UK)             │    │
│  │ - Indices para performance                              │    │
│  │ - PostgreSQL Enums personalizados                        │    │
│  └──────────────────────────────────────────────────────────┘    │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ Identity (ASP.NET Core Identity)                         │    │
│  │ - UserManager, RoleManager                              │    │
│  │ - JWT Token Generation                                  │    │
│  │ - Roles: ADMIN, CLIENT                                  │    │
│  └──────────────────────────────────────────────────────────┘    │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ DependencyInjection                                      │    │
│  │ - Configuración de servicios                            │    │
│  │ - Mapeo de enums PostgreSQL                             │    │
│  └──────────────────────────────────────────────────────────┘    │
└────────────────┬─────────────────────────────────────────────────┘
                 │
┌────────────────┴─────────────────────────────────────────────────┐
│              PostgreSQL Database                                  │
│  Tablas:                                                          │
│  - AspNetUsers (Identity)                                        │
│  - AspNetRoles (Identity)                                        │
│  - tenants, accounts, transactions                               │
│  - exchange_rates, audit_logs, idempotency_records               │
└─────────────────────────────────────────────────────────────────┘
```

### Flujo de Multi-Tenancy

1. **Registro**: Primer usuario → ADMIN, siguientes → CLIENT
2. **Creación de Tenant**: Solo ADMIN puede crear tenants
3. **Aislamiento**: Cada tenant tiene sus datos separados (constraints FK a tenant_id)
4. **Operaciones**: Dentro de cada tenant, usuarios pueden crear cuentas y hacer transacciones

### Características Clave

- **JWT Authentication**: Tokens seguros con configuración HMAC-256
- **Multi-Tenancy**: Arquitectura de base de datos compartida con aislamiento de datos
- **Auditoría**: AuditLog con valores old/new en JSONB
- **Idempotencia**: Prevención de transacciones duplicadas
- **Enums PostgreSQL**: Validación de datos a nivel de BD
- **Validaciones**: Constraints de BD (balance >= 0, currencies válidas, etc)

## Estructura del Repositorio

```
hydra-bank/
├── .git/                          # Control de versiones
├── .gitignore                     # Archivos ignorados
├── Directory.Build.props          # Propiedades globales del proyecto
├── Hydra.slnx                     # Solución Visual Studio
├── README.md                      # Este archivo
│
├── Hydra.Api/                     # CAPA DE PRESENTACIÓN (API REST)
│   ├── Program.cs                 # Configuración de la aplicación (Startup)
│   ├── Hydra.Api.csproj          # Proyecto C#
│   ├── Hydra.Api.http            # Archivo de pruebas HTTP (REST Client)
│   ├── appsettings.json          # Configuración producción
│   ├── appsettings.Development.json  # Configuración desarrollo
│   │
│   ├── Controllers/
│   │   ├── AuthController.cs      # Endpoints: Register, Login, GenerateToken
│   │   └── TenantsController.cs   # Endpoints: CreateTenant
│   │
│   ├── Middlewares/               # Middlewares personalizados (vacío actualmente)
│   │
│   └── Properties/
│       └── launchSettings.json    # Configuración de lanzamiento
│
├── Hydra.Application/             # CAPA DE APLICACIÓN (Modelos de Datos)
│   ├── Hydra.Application.csproj
│   │
│   └── DTOs/                      # Data Transfer Objects
│       ├── RegisterDto.cs         # Modelo para registro de usuarios
│       ├── LoginDto.cs            # Modelo para login
│       ├── CreateTenantDto.cs     # Modelo para crear tenant
│       ├── CreateRoleDto.cs       # Modelo para crear roles
│       └── AssignRoleDto.cs       # Modelo para asignar roles
│
├── Hydra.Domain/                  # CAPA DE DOMINIO (Lógica de Negocio)
│   ├── Hydra.Domain.csproj
│   │
│   ├── Entities/                  # Entidades del dominio
│   │   ├── Tenant.cs              # Información del cliente/banco
│   │   ├── User.cs                # Usuarios por tenant
│   │   ├── Account.cs             # Cuentas bancarias
│   │   ├── Transaction.cs         # Transacciones (depósitos, retiros, transferencias)
│   │   ├── ExchangeRate.cs        # Tasas de cambio
│   │   ├── AuditLog.cs            # Registro de auditoría
│   │   └── IdempotencyRecord.cs   # Registros de idempotencia
│   │
│   └── Enums/                     # Enumeraciones
│       ├── UserRole.cs            # ADMIN, CLIENT
│       ├── AccountStatus.cs       # ACTIVE, INACTIVE, BLOCKED
│       ├── TransactionType.cs     # DEPOSIT, WITHDRAW, TRANSFER
│       ├── TransactionStatus.cs   # PENDING, SUCCESS, FAILED
│       ├── FeeTypeEnum.cs         # FIXED, PERCENTAGE
│       └── IdempotencyState.cs    # PROCESSING, COMPLETED
│
├── Hydra.Infrastructure/          # CAPA DE INFRAESTRUCTURA (Persistencia)
│   ├── Hydra.Infrastructure.csproj
│   │
│   ├── DATA/
│   │   ├── BankOsDbContext.cs     # DbContext (EF Core)
│   │   │                          # - Configuración de todas las entidades
│   │   │                          # - Constraints de base de datos
│   │   │                          # - Índices
│   │   │                          # - Relaciones
│   │   │
│   │   ├── DependencyInjection.cs # Configuración de servicios
│   │   │                          # - AddDbContext
│   │   │                          # - AddIdentity
│   │   │                          # - Mapeo de enums PostgreSQL
│   │   │
│   │   └── Migrations/            # Migraciones de base de datos
│   │       ├── 20260610014410_InitialCreate.*
│   │       ├── 20260610014734_InitialIdentity.*
│   │       ├── 20260610015447_secondMigrations.*
│   │       └── BankOsDbContextModelSnapshot.cs
│   │
│   └── DOC/                       # Documentación
│       ├── Doc Dbhydra.md         # Documentación de la BD
│       ├── InitialCreate.sql      # Script SQL inicial
│       └── script.sql             # Scripts adicionales
│
└── [build folders]
    ├── bin/                       # Binarios compilados
    └── obj/                       # Objetos compilados
```

### Explicación de Carpetas

| Carpeta | Propósito |
|---------|-----------|
| **Hydra.Api** | REST API con JWT, Swagger, controladores |
| **Hydra.Application** | DTOs para comunicación entre capas |
| **Hydra.Domain** | Entidades de negocio e interfaces de repositorio |
| **Hydra.Infrastructure** | EF Core, migraciones, Identity, inyección de dependencias |

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
