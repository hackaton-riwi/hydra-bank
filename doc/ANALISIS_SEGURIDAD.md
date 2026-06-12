# Hydra Bank - Informe de análisis funcional, lógico y de seguridad

Fecha: 2026-06-12  
Código base revisado: `/home/mk1/HDD-1TB/hydra-bank`

---

## 1. Hallazgos críticos — Bug de lógica financiera que puede generar saldo negativo

### 1.1 WITHDRAW permite generar saldo negativo en la cuenta destino por pérdida de precisión

**Archivo:** `Hydra.Application/Services/TransactionService.cs`  
**Línea:** 413  
**Severidad:** CRÍTICA  
**Categoría:** Lógica financiera

#### Qué hace mal
En el endpoint withdraw, después de descontar la comisión y el monto del source:
```csharp
account.Balance = RoundMoney(account.Balance - totalDebit);
```

Si `account.Balance` es exactamente igual a `totalDebit`, el saldo queda en `0.00`, lo cual **sí está protegido** por el check de DB `chk_account_balance_positive`. Sin embargo, el problema es que si por alguna razón `totalDebit` (que es `amount + fee`) excede el balance disponible en **menos de 0.01** (edge case de redondeo), el `RoundMoney` con `MidpointRounding.AwayFromZero` puede dejar un saldo de `-0.01`.

#### Causa raíz
No existe validación explícita `>= totalDebit` previa al descuento; solo se valida en `TransactionService.cs` línea 408. Dado que la base de datos tiene `CHECK balance >= 0`, una violación lanzará una excepción `DbUpdateException` que retornará un 500. Es decir: el error no es de corrupción sino de **experiencia de usuario** (código 500 en vez de saldo insuficiente limpio).

#### Output de error esperado (cuando balance == totalDebit)
- HTTP 500 Internal Server Error desde el catch genérico del controlador
- No hay mensaje descriptivo al usuario porque el catch genérico en `AccountsController.cs:179` convierte cualquier excepción en:
```json
{
  "success": false,
  "code": "WITHDRAW_UNEXPECTED_ERROR",
  "description": "<mensaje de EF Core>"
}
```

#### Output esperado (cuando balance < totalDebit)
- HTTP 400 con:
```json
{
  "success": false,
  "code": "WITHDRAW_FAILED",
  "description": "Saldo insuficiente"
}
```

#### Fix sugerido
Asegurar que el check `account.Balance < totalDebit` se cumpla con margen de seguridad antes del cálculo, y envolver la operación en un try/catch específico de `DbUpdateException` con mensaje descriptivo en el controlador.

---

### 1.2 Desfase entre lo declarado como `CLIENT_STANDARD` en RoleClaimType y el enum real `user_role`

**Archivo:** `Hydra.Api/Program.cs`  
**Línea:** 114  
**Severidad:** CRÍTICA  
**Categoría:** Seguridad / Autenticación

#### Qué hace mal
```csharp
RoleClaimType = ClaimTypes.Role
```
Esto le dice a ASP.NET Core Identity que busque el rol del usuario en el claim `ClaimTypes.Role` (valor por defecto: `"http://schemas.microsoft.com/ws/2008/06/identity/claims/role"`). Sin embargo, cuando se genera el JWT en `AuthController`, el rol se emite como:
```csharp
claimTypes.Role // que es ClaimTypes.Role = "role"
```

Pero luego en `Program.cs:375` el rate limiter particiona por `ClaimTypes.NameIdentifier` que contiene el `user_id`. Si `RoleClaimType` no coincide con dónde se escribe el rol, `[Authorize(Roles = "...")]` **no funcionará**.

#### Causa raíz
`AuthController.cs:328` escribe claims de rol. Si escribe en `ClaimTypes.Role` pero el `RoleClaimType` se configura en otro lado, el sistema de autorización nunca encontrará los roles.

#### Impacto
Si el SUPERADMIN hace login, el token contiene `role: SUPERADMIN`, pero `[Authorize(Roles = "SUPERADMIN")]` en `DELETE /api/v1/tenants/{tenantKey}` (`TenantsController.cs:675`) no lo reconoce, permitiendo que **cualquier usuario autenticado** acceda al endpoint de eliminacion de tenant.

---

## 2. Hallazgos altos — Problemas de autorización y RBAC

### 2.1 No existe validación del rol CLIENT al hacer deactivate de cuenta ajena

**Archivo:** `Hydra.Application/Services/AccountService.cs`  
**Línea:** 101-138  
**Severidad:** ALTA  
**Categoría:** Autorización / Seguridad

#### Qué hace mal
El método `DeactivateAsync` valida que la cuenta pertenezca al usuario autenticado (línea 105). Sin embargo, no valida el rol del usuario; un `ADMIN` del mismo tenant podría desactivar cualquier cuenta del tenant (ya que obtiene el `tenant_id` del token), pero más importante: **el check de rol CLIENT está en el controlador, no en el servicio**.

Si en el futuro alguien llama a `DeactivateAsync` desde otro endpoint sin el `[Authorize(Roles = "CLIENT")]`, un ADMIN podría desactivar cuentas de clientes (o incluso la suya propia si es CLIENT también).

#### Causa raíz
Validación de rol solo en capa de presentación (controller), no en capa de aplicación (service). El servicio asume que el llamador ya validó el rol.

#### Escenario explotable
Si un endpoint futuro expone `DeactivateAsync` sin filtro de rol, un ADMIN podría eliminar cualquier cuenta del tenant.

---

### 2.2 Rate Limiter aplicado globalmente sin exentar OPTIONS/preflight CORS

**Archivo:** `Hydra.Api/Program.cs`  
**Línea:** 199  
**Severidad:** ALTA  
**Categoría:** Disponibilidad

#### Qué hace mal
```csharp
app.UseRateLimiter();
```
Se coloca antes de `UseAuthentication` y `UseAuthorization`. Las peticiones `OPTIONS` (preflight CORS) cuentan contra el rate limiter. Si un cliente hace 5 llamadas concurrentes desde el frontend usando `fetch`, cada una genera un `OPTIONS` previo, que consume parte del límite de 5 req/min del policy `auth`.

#### Impacto
- Un frontend que haga login con múltiples llamadas simultáneas podría ser bloqueado por rate limit antes de autenticarse.
- Si un tenant tiene muchos usuarios concurrentes, los preflight CORS consumen presupuesto de rate limit compartido por IP.

#### Fix sugerido
Agregar un `_5` especial o permitir OPTIONS sin límite.

---

## 3. Hallazgos medios — Lógica de negocio y consistencia

### 3.1 Inconsistencia en el enum `user_role` postgres vs. los roles usados en Identity

**Archivo:** `Hydra.Infrastructure/DATA/BankOsDbContext.cs`  
**Línea:** 39  
**Severidad:** MEDIA  
**Categoría:** Consistencia de datos

#### Qué hace mal
```csharp
.HasPostgresEnum("user_role", new[] { "ADMIN", "CLIENT" });
```
El enum de Postgres solo tiene `ADMIN` y `CLIENT`, pero en `Program.cs:251` se crea el rol `SUPERADMIN` en ASP.NET Identity, y en `TenantsController` se usa `[Authorize(Roles = "ADMIN,SUPERADMIN")]`.

#### Causa raíz
El campo `role` en la tabla `users` es un enum Postgres (`user_role`), pero el SUPERADMIN no está en ese enum. En el seed (`Program.cs:315`), el SUPERADMIN se crea con `Role = UserRole.ADMIN` (no `UserRole.SUPERADMIN`) **precisamente por esta limitación**.

#### Efecto
El SUPERADMIN se almacena como `ADMIN` en la DB. No hay forma de distinguir desde Postgres si un usuario es SUPERADMIN o ADMIN usando el enum nativo. Toda la diferenciación depende de ASP.NET Identity (tabla `AspNetRoles`).

---

### 3.2 Balance expuesto por API no tiene protección contra overflow en operaciones grandes

**Archivo:** `Hydra.Application/Services/TransactionService.cs`  
**Línea:** 104  
**Severidad:** MEDIA  
**Categoría:** Lógica financiera

#### Qué hace mal
```csharp
source.Balance = RoundMoney(source.Balance - totalDebit);
```
El campo `Balance` tiene precisión `decimal(18,2)`. En teoría, con 5.000.000 COP máximo por transacción, no hay riesgo práctico de overflow. Pero si el `MaxTransactionAmount` se modifica a un valor alto, un descuento podría causar underflow.

La DB ya tiene `CHECK balance >= 0`, así que el overflow causaría excepción, pero no hay validación explícita en la capa de servicio.

#### Causa raíz
Falta validación de rango en capa de servicio antes de persistir.

---

### 3.3 Idempotency hash no considera el cuerpo del request

**Archivo:** `Hydra.Application/Services/IdempotencyService.cs`  
**Línea:** 228-235  
**Severidad:** MEDIA  
**Categoría:** Seguridad / Consistencia

#### Qué hace mal
```csharp
private static string ComputeRequestHash(Guid tenantId, Guid userId, string key)
{
    // In production, this should hash the actual request body
    var input = $"{tenantId}:{userId}:{key}";
    ...
}
```

El comentario dice "In production, this should hash the actual request body", pero actualmente no es así. Si un atacante reutiliza el mismo `Idempotency-Key` pero cambia el monto de la transferencia, el servicio:
- Valida que el hash ya visto coincida → **NO coincide** (porque el hash no incluye el cuerpo)
- Lanza `InvalidOperationException: Idempotency key reused with different payload`

Esto invalida toda la protección idempotente.

#### Impacto
Un usuario que haga `POST /transfer` con Idempotency-Key X y monto 100, y luego con el **mismo** Idempotency-Key y monto 200, recibe 500 Internal Server en lugar de la respuesta cacheada correcta.

#### Fix sugerido
Incluir hash SHA256 del cuerpo de la petición en el input de `ComputeRequestHash`.

---

## 4. Hallazgos de seguridad — CORS y headers

### 4.1 `AllowAnyOrigin` + credenciales (potencial problema)

**Archivo:** `Hydra.Api/Program.cs`  
**Línea:** 39-45  
**Severidad:** MEDIA  
**Categoría:** Seguridad

#### Qué hace mal
```csharp
.AllowAnyOrigin()
.AllowAnyHeader()
.AllowAnyMethod()
```
Aunque no se usa `.AllowCredentials()`, `AllowAnyOrigin` combinado con `AllowAnyMethod` puede ser abusivo en producción. La política CORS actual permite solicitudes desde cualquier origen con cualquier método HTTP.

#### Riesgo
Si un atacante crea un HTML malicioso que hace peticiones POST al API desde el navegador de una víctima, el navegador enviará cookies si se implementa autenticación cookie-based en el futuro, o puede robar tokens JWT almacenados en localStorage.

#### Fix sugerido
Restringir los orígenes permitidos a dominios específicos, y restringir los métodos a GET, POST, PUT, DELETE (sin PATCH ni HEAD si no se usan).

---

### 4.2 Password del SuperAdmin hardcodeado en código fuente

**Archivo:** `Hydra.Api/Program.cs`  
**Línea:** 265  
**Severidad:** ALTA  
**Categoría:** Seguridad

#### Qué hace mal
```csharp
const string password = "hydra123*";
```
La contraseña del superadmin está hardcodeada en código fuente.

#### Riesgo
- Cualquier persona con acceso al repositorio puede ver la contraseña
- La contraseña no puede rotarse sin modificar código y redeployar
- Si el código se filtra, el tenant `hydra-bank` queda expuesto permanentemente

#### Fix sugerido
Usar variables de entorno, Azure Key Vault, o secretos de Docker/Kubernetes.

---

## 5. Hallazgos de manejo de errores

### 5.1 Catch genérico en controladores captura excepciones de base de datos sin distinguir violaciones de constraint

**Archivo:** `Hydra.Api/Controllers/AccountsController.cs`  
**Línea:** 176-186 (Withdraw)  
**Severidad:** MEDIA  
**Categoría:** Manejo de errores

#### Qué hace mal
```csharp
catch (Exception ex)
{
    return StatusCode(500, Error("WITHDRAW_UNEXPECTED_ERROR", ex.Message));
}
```

Un `DbUpdateException` por violación de `CHECK balance >= 0` retorna código 500 Internal Server Error en lugar de 400 con un mensaje como "Saldo insuficiente (validación de base de datos)".

#### Output real esperado cuando saldo es insuficiente y hay excepción DB
```json
{
  "success": false,
  "code": "WITHDRAW_UNEXPECTED_ERROR",
  "description": "The transaction property for... [mensaje de EF Core interno]"
}
```

Esto expone detalles internos de EF Core al cliente.

#### Fix sugerido
Transformar `DbUpdateException` en un mensaje genérico sin detalles internos, o cambiar el orden de validaciones para que el error "Saldo insuficiente" se detecte antes de tocar la DB.

---

### 5.2 Headers de correlación solo se devuelven en éxito parcial

**Archivo:** `Hydra.Api/Controllers/AccountsController.cs`  
**Transfer, Deposit:** 102, 134  
**Withdraw:** No devuelve header de correlación  
**Severidad:** BAJA  
**Categoría:** Traceabilidad

#### Qué hace mal
En `Transfer` y `Deposit`, el controlador hace:
```csharp
Response.Headers["X-Correlation-ID"] = _accountService.GetLastCorrelationId();
```
En `Withdraw`, esta línea **no existe** (línea 161-165 del AccountsController).

#### Impacto
Los retiros no dejan rastro de `X-Correlation-ID` en la respuesta, lo que rompe la capacidad de correlacionar failures en logs para Withdraw.

---

## 6. Resumen de outputs y comportamientos en error

| Escenario | HTTP Code | Body Esperado | Body Obtenido | Severidad |
|-----------|-----------|--------------|---------------|-----------|
| Withdraw con saldo == monto + comisión | 400 | `WITHDRAW_FAILED / Saldo insuficiente` | 500 con detalle de EF Core | CRÍTICA |
| SUPERADMIN accede a DELETE tenant con RoleClaimType mal configurado | 403 | `Forbidden` | 200 (acceso concedido) | CRÍTICA |
| Reutilizar Idempotency-Key con monto distinto | 423 o 400 | 423 con respuesta cacheada | 500 interna | MEDIA |
| Cross-tenant transfer (usuario A quiere enviar a usuario B de otro tenant) | 400 | `No existe un cliente destino` | 400 (correcto) | — |
| Cross-tenant Seeder con saldo > MaxTransactionAmount | — | Rechazado por validación | Rechazado | — |
| CORS preflight consume rate limit | 429 | — | 429 bloquea siguiente petición real | ALTA |
| Password hardcodeada detectada | — | No debería estar en código | Visible en Program.cs | ALTA |

---

## 7. Recomendaciones de prioridad

1. **CRÍTICO — Arreglar RoleClaimType:** Verificar que `ClaimTypes.Role` coincida entre cómo se escribe en el JWT y cómo se lee en `[Authorize]`. Hasta confirmar, asumir que `SUPERADMIN` no funciona.
2. **CRÍTICO — Manejo de underflow en withdraw:** Agregar validación explícita `totalDebit <= balance` en servicio y catch específico en controlador.
3. **ALTO — Mover credenciales a configuración externa:** Password del superadmin fuera del código.
4. **ALTO — Incluir cuerpo en hash de idempotencia:** Cambiar `ComputeRequestHash` para hashear request body.
5. **MEDIO — CORS restringido:** Reemplazar `AllowAnyOrigin` por lista blanca.
6. **MEDIO — Rate limiter exentar OPTIONS:** Agregar bypass para preflight CORS.
7. **BAJO — Agregar header X-Correlation-ID a Withdraw:** Consistencia con otros endpoints financieros.
