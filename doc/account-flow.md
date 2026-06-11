# Flujo de usuario, cuenta y recarga

## Objetivo actual

El usuario cliente se registra con:

- `tenantSlug`
- `fullName`
- `documentNumber`
- `email`
- `password`

Al registrarse, el sistema crea automaticamente:

- El usuario bancario en `users`.
- Una cuenta en `accounts`.
- Un `accountNumber` unico dentro del tenant.
- Saldo inicial en `0`.
- Moneda fija `COP`.

El registro no inicia sesion automaticamente. Despues de registrarse, el cliente debe llamar `POST /api/v1/auth/login` para obtener su token.

Antes de registrarse, el frontend puede llamar `GET /api/v1/tenants` para mostrar los tenants disponibles. El usuario escoge uno y el frontend envia el `tenantSlug` seleccionado en el registro y el login.

## Reglas actuales

- Un documento solo puede estar registrado una vez por tenant.
- Un usuario/documento solo puede tener una cuenta por tenant.
- La recarga solo aumenta el saldo de la cuenta propia.
- La recarga no crea transacciones todavia.
- Redis se conserva configurado para cache distribuido, aunque el flujo actual no depende de cache.

## Endpoints actuales

### Listar tenants disponibles

`GET /api/v1/tenants`

Respuesta relevante:

```json
{
  "tenants": [
    {
      "id": "00000000-0000-0000-0000-000000000000",
      "name": "Manhattan Trust Bank",
      "slug": "manhattan-trust",
      "mainCurrency": "COP"
    }
  ]
}
```

Este endpoint es publico para que el cliente pueda escoger a que tenant registrarse. No devuelve correos de administradores ni datos internos sensibles.

### Registrar cliente y crear cuenta

`POST /api/v1/auth/register`

```json
{
  "tenantSlug": "manhattan-trust",
  "fullName": "Cliente Uno",
  "documentNumber": "123456789",
  "email": "cliente@manhattan.test",
  "password": "Client123"
}
```

Respuesta relevante:

```json
{
  "success": true,
  "code": "CLIENT_REGISTERED",
  "user": {
    "fullName": "Cliente Uno",
    "documentNumber": "123456789"
  },
  "account": {
    "accountNumber": "0000000000",
    "balance": 0,
    "currency": "COP"
  }
}
```

### Login

`POST /api/v1/auth/login`

Devuelve el usuario y la cuenta asociada si ya existe.

### Recargar cuenta propia

`POST /api/v1/accounts/recharge`

Requiere token `CLIENT`.

```json
{
  "amount": 50000
}
```

La recarga valida que:

- El monto sea mayor que cero.
- El usuario autenticado tenga cuenta.
- La cuenta este activa.

## Transferencias futuras

Cuando se conecte el modulo de transferencias, el flujo recomendado es:

1. Buscar la cuenta origen usando el usuario autenticado.
2. Buscar la cuenta destino por `documentNumber` o `accountNumber` dentro del mismo tenant.
3. Validar que origen y destino sean cuentas distintas y activas.
4. Validar saldo suficiente.
5. Ejecutar el debito y credito dentro de una transaccion de base de datos con aislamiento fuerte.
6. Crear un registro en `transactions` para auditoria.
7. Si se reintroduce idempotencia, aplicarla solo a operaciones de transferencia y pagos, no al registro ni a la recarga simple.

Redis no debe ser la fuente de verdad para saldos. Los saldos deben vivir en PostgreSQL. Redis puede ayudar despues para cache de limites o datos de lectura, pero no para confirmar movimientos de dinero.
