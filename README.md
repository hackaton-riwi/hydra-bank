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

## API para crear tenants

El proyecto tiene autenticación con JWT e Identity. El primer usuario que se registra queda con rol `ADMIN`; los siguientes quedan como `CLIENT`.

Solo un usuario con rol `ADMIN` puede crear tenants.

### 1. Registrar el primer admin

```http
POST /api/auth/register
Content-Type: application/json

{
  "email": "admin@hydra.test",
  "password": "Admin123"
}
```

La respuesta incluye un `token`. Ese token se usa como Bearer token.

En Swagger, abre el botón `Authorize` y pega solo el valor del token, sin escribir `Bearer`. En Postman o curl sí debes enviar el header completo: `Authorization: Bearer TU_TOKEN`.

### 2. Crear tenant

```http
POST /api/tenants
Authorization: Bearer TU_TOKEN_ADMIN
Content-Type: application/json

{
  "name": "Manhattan Trust Bank",
  "slug": "manhattan-trust",
  "mainCurrency": "USD",
  "maxTransactionAmount": 5000000,
  "feeType": "FIXED",
  "feeValue": 2.5,
  "webhookUrl": "https://example.com/webhooks/hydra"
}
```

`feeType` acepta `FIXED` o `PERCENTAGE`. Si usas `PERCENTAGE`, `feeValue` no puede ser mayor a `100`.

El `slug` debe ser único y solo puede tener letras minúsculas, números y guiones, por ejemplo `mi-banco`.

### Respuestas esperadas

- `201 Created`: tenant creado.
- `400 Bad Request`: datos inválidos.
- `401 Unauthorized`: no enviaste token válido.
- `403 Forbidden`: el usuario no tiene rol `ADMIN`.
- `409 Conflict`: ya existe un tenant con ese `slug`.
