# BankOS — Sistema Bancario Multitenant como Servicio

Plataforma bancaria en la nube (Backend .NET) donde cada institución financiera opera como un **tenant independiente** con clientes, cuentas, divisas y reglas de negocio totalmente aislados.

## Stack Tecnológico

| Capa | Tecnología |
|------|-----------|
| Runtime | .NET 10 |
| API | ASP.NET Core Minimal + Controllers |
| ORM | Entity Framework Core + Npgsql |
| BD | PostgreSQL 16 (enums, RLS, JSONB, check constraints) |
| Cache/Idempotencia | Redis 7 (SET NX, expiración 24h) |
| Auth | ASP.NET Core Identity + JWT (HMAC-SHA256) |
| Documentación | Swagger / OpenAPI 3.0 |

## Requisitos

- Docker + Docker Compose
- .NET 10 SDK (desarrollo local)
- PostgreSQL 16 (vía Docker)
- Redis 7 (vía Docker)

## Inicio Rápido

```bash
# 1. Clonar repositorio
git clone <repo-url>
cd hydra-bank

# 2. Iniciar servicios de infraestructura
docker compose up -d

# 3. Ejecutar migraciones
dotnet ef database update --project Hydra.Infrastructure --startup-project Hydra.Api

# 4. Iniciar API
dotnet run --project Hydra.Api

# 5. Abrir Swagger
open http://localhost:5000/swagger
```

## Super Admin por Defecto

Al iniciar la aplicación por primera vez, se crea automáticamente:

```
Tenant:   Hydra Bank (slug: hydra-bank)
Email:    admin@hydra.test
Password: hydra123*
Rol:      SUPERADMIN
```

## Arquitectura

```
┌─────────────────────────────────────────────────────────────┐
│                    HYDRA.API (Presentación)                  │
│  AuthController  TenantsController  AccountsController       │
│  TransactionsController (oculto Swagger)                     │
├─────────────────────────────────────────────────────────────┤
│              HYDRA.APPLICATION (Aplicación)                  │
│  AccountService     TransactionService                       │
│  IdempotencyService WebhookNotifier                          │
│  DTOs, Interfaces                                            │
├─────────────────────────────────────────────────────────────┤
│               HYDRA.DOMAIN (Dominio)                         │
│  Tenant, User, Account, Transaction, AuditLog                │
│  IdempotencyRecord, Enums, Exceptions                        │
├─────────────────────────────────────────────────────────────┤
│           HYDRA.INFRASTRUCTURE (Persistencia)                │
│  BankOsDbContext (EF Core + Npgsql)                          │
│  Migraciones, DependencyInjection                            │
├─────────────────────────────────────────────────────────────┤
│  PostgreSQL 16          Redis 7                              │
│  (Datos + Enums + RLS)  (Idempotencia + Cache)              │
└─────────────────────────────────────────────────────────────┘
```

## Cumplimiento vs Requerimientos

| # | Requerimiento | Estado |
|---|--------------|--------|
| 1A | Registro de Tenants | ✅ |
| 1A | Aislamiento Estricto | ✅ FK compuestas + RLS |
| 1A | Configuración por Tenant | ✅ Límite, comisión, moneda |
| 1A | Webhooks | ✅ Fire-and-forget post-commit |
| 1B | Auth JWT + Claims | ✅ tenant_id, user_id, role |
| 1B | Rate Limiting | ✅ Auth 5/min, Financial 30/min |
| 1B | Auditoría Inmutable | ✅ AuditLog append-only + trigger |
| 1C | Creación Cuentas | ✅ Vinculadas a cliente + tenant |
| 1C | Soft Delete | ✅ Estado INACTIVE/BLOCKED |
| 1D | Depósito/Retiro/Transferencia | ✅ Con comisión y validaciones |
| 1D | Multimoneda | ✅ Tasas estáticas por tenant |
| 1E | Idempotencia | ✅ Redis SET NX + replay exacto |
| 1E | X-Correlation-ID | ✅ Propagado header → BD → response |
| 1F | Historial + Paginación | ✅ Limit/offset + filtros |
| 1G | API Versionada | ✅ /api/v1/... |
| 2 | Docker Compose | ✅ app + postgres + redis |

## Documentación

| Documento | Descripción |
|-----------|-------------|
| [doc/ARCHITECTURE.md](doc/ARCHITECTURE.md) | Arquitectura detallada del sistema |
| [doc/API.md](doc/API.md) | Referencia completa de la API REST |
| [doc/DATABASE.md](doc/DATABASE.md) | Modelo de datos y esquema BD |
| [doc/AUDITORIA.md](doc/AUDITORIA.md) | Estado del proyecto vs requerimientos |
| [doc/QA.md](doc/QA.md) | Plan de pruebas y escenarios de demo |
| [doc/DATABASE.md](doc/DATABASE.md) | Documentación de base de datos |
| /swagger | Swagger UI (en ejecución) |

## Escenarios de Demo

El sistema está preparado para demostrar en vivo:

1. **Dos tenants en paralelo sin cruce de datos** — Misma cuenta "12345" en Tenant A y B; cada uno ve solo sus datos.
2. **Reintento con Idempotency-Key** — Transferencia interrumpida y reintentada con la misma key; sin doble débito.
3. **Transferencia multimoneda** — Conversión USD → COP con tasa estática del tenant + comisión parametrizada.

## Proyectos

| Proyecto | Descripción |
|----------|-------------|
| `Hydra.Api` | API REST (Controllers, Middleware, Program.cs) |
| `Hydra.Application` | Lógica de aplicación (Servicios, DTOs, Interfaces) |
| `Hydra.Domain` | Entidades, Enums, Excepciones del dominio |
| `Hydra.Infrastructure` | Persistencia (DbContext, Migraciones, DI) |
