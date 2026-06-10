# Documentación de Base de Datos - BankOS

  ## 1. Objetivo de la Base de Datos

  La base de datos de BankOS fue diseñada para soportar un sistema bancario multitenant, donde varias
  instituciones financieras pueden operar dentro de la misma plataforma sin compartir ni exponer información
  entre ellas.

  El objetivo principal del diseño es garantizar:

  - Aislamiento estricto entre tenants.
  - Integridad financiera.
  - Trazabilidad completa de operaciones.
  - Soporte para múltiples monedas.
  - Control de idempotencia en operaciones críticas.
  - Auditoría inmutable.
  - Compatibilidad con EF Core mediante claves primarias simples.

  ———

  ## 2. Modelo Multitenant

  La tabla central del sistema es tenants.

  Cada tenant representa una institución financiera independiente, por ejemplo un banco, cooperativa o fintech.

  tenants

  Contiene la configuración propia de cada institución:

  - name: nombre del tenant.
  - slug: identificador único usado para login o routing.
  - main_currency: moneda principal.
  - max_transaction_amount: monto máximo permitido por transacción.
  - fee_type: tipo de comisión, fija o porcentual.
  - fee_value: valor de la comisión.
  - webhook_url: endpoint para notificaciones externas.

  ### ¿Por qué se hizo así?

  Porque cada institución debe tener sus propias reglas de negocio. Un tenant puede operar en COP con comisión
  fija, mientras otro puede operar en USD con comisión porcentual.

  ———

  ## 3. Aislamiento Físico por Tenant

  Todas las tablas sensibles incluyen tenant_id.

  Ejemplos:

  users.tenant_id
  accounts.tenant_id
  transactions.tenant_id
  exchange_rates.tenant_id
  audit_logs.tenant_id
  idempotency_records.tenant_id

  Esto permite que cada registro pertenezca explícitamente a una institución.

  Además, se agregaron constraints compuestas como:

  UNIQUE (tenant_id, id)

  y llaves foráneas compuestas como:

  FOREIGN KEY (tenant_id, owner_id)
  REFERENCES users(tenant_id, id)

  ### ¿Por qué se hizo así?

  Para evitar cruces accidentales entre tenants.

  Por ejemplo, una cuenta del tenant A no puede pertenecer a un usuario del tenant B. Aunque ambos IDs existan,
  la base de datos rechaza la relación si el tenant_id no coincide.

  Esto es más fuerte que validar solo en backend, porque la propia base de datos protege la integridad.

  ———

  ## 4. Usuarios y Roles

  La tabla users almacena los usuarios del sistema.

  Cada usuario pertenece obligatoriamente a un tenant y tiene un rol:

  user_role AS ENUM ('ADMIN', 'CLIENT')

  Roles:

  - ADMIN: puede administrar usuarios, cuentas, configuración y consultar auditoría.
  - CLIENT: solo puede operar y consultar sus propias cuentas.

  ### ¿Por qué se hizo así?

  Porque el sistema necesita separar usuarios administrativos de clientes bancarios. El rol permite aplicar
  permisos tanto en backend como en políticas RLS dentro de PostgreSQL.

  ———

  ## 5. Cuentas Bancarias

  La tabla accounts representa las cuentas de los clientes.

  Campos importantes:

  - owner_id: usuario titular de la cuenta.
  - balance: saldo actual.
  - currency: moneda de la cuenta.
  - status: estado de la cuenta.
  - deactivated_at: fecha de desactivación lógica.

  Estados posibles:

  account_status AS ENUM ('ACTIVE', 'INACTIVE', 'BLOCKED')

  ### ¿Por qué usamos soft delete?

  No se eliminan físicamente las cuentas porque las transacciones históricas deben seguir siendo válidas. En
  banca, borrar una cuenta rompería la trazabilidad.

  Por eso una cuenta se desactiva cambiando su estado, pero el registro permanece.

  ———

  ## 6. Transacciones

  La tabla transactions registra depósitos, retiros y transferencias.

  Tipos:

  transaction_type AS ENUM ('DEPOSIT', 'WITHDRAW', 'TRANSFER')

  Estados:

  transaction_status AS ENUM ('PENDING', 'SUCCESS', 'FAILED')

  Campos clave:

  - source_account_id: cuenta origen.
  - destination_account_id: cuenta destino.
  - original_amount: monto solicitado.
  - converted_amount: monto convertido si aplica.
  - exchange_rate: tasa usada.
  - fee_amount: comisión cobrada.
  - idempotency_key: clave de idempotencia.
  - correlation_id: trazabilidad de la petición.

  Se agregó una constraint para validar la forma correcta de cada operación:

  DEPOSIT: sin cuenta origen, con cuenta destino
  WITHDRAW: con cuenta origen, sin cuenta destino
  TRANSFER: con cuenta origen y destino diferentes

  ### ¿Por qué se hizo así?

  Porque cada tipo de operación tiene reglas estructurales distintas. La base de datos impide guardar
  transacciones incoherentes, por ejemplo un retiro sin cuenta origen o una transferencia hacia la misma
  cuenta.

  ———

  ## 7. Multimoneda

  La tabla exchange_rates almacena tasas estáticas por tenant.

  Cada tenant define sus propias tasas:

  from_currency
  to_currency
  rate

  Ejemplo:

  USD -> COP = 4000
  COP -> USD = 0.00025

  ### ¿Por qué por tenant?

  Porque cada institución puede definir sus propias reglas de conversión. Esto cumple el requisito de
  configuración independiente por tenant.

  También se bloquean pares inválidos:

  from_currency <> to_currency

  ———

  ## 8. Idempotencia

  La tabla idempotency_records controla que una operación financiera no se ejecute dos veces si el cliente
  reintenta la misma petición.

  Cada registro se asocia a:

  tenant_id + user_id + idempotency_key

  Campos importantes:

  - request_hash: huella del request original.
  - response_body: respuesta original.
  - status_code: código HTTP original.
  - state: PROCESSING o COMPLETED.
  - expires_at: vencimiento de la clave.

  ### ¿Por qué se hizo así?

  En operaciones bancarias, si una transferencia falla por red y el usuario reintenta, no debe cobrarse dos
  veces.

  Con esta tabla:

  - Si la operación ya terminó, se devuelve la misma respuesta.
  - Si está en proceso, se responde conflicto.
  - Si es nueva, se procesa una sola vez.

  ———

  ## 9. Auditoría Inmutable

  La tabla audit_logs registra acciones administrativas:

  - creación de usuarios,
  - cambios de configuración,
  - bloqueo o desactivación de cuentas,
  - cambios sensibles.

  Campos:

  - action
  - old_value
  - new_value
  - created_at
  - user_id
  - tenant_id

  Se agregó un trigger:

  prevent_audit_mutation()

  Este impide UPDATE y DELETE sobre audit_logs.

  ### ¿Por qué se hizo así?

  Porque la auditoría debe ser inmutable. Un administrador no debería poder modificar o borrar evidencia
  histórica de cambios sensibles.

  ———

  ## 10. Row Level Security - RLS

  Se activó RLS en todas las tablas críticas:

  ALTER TABLE ... ENABLE ROW LEVEL SECURITY;

  Las políticas usan variables de sesión:

  app.tenant_id
  app.user_id
  app.role

  El backend debe configurarlas por cada request autenticado:

  SELECT set_config('app.tenant_id', '<tenant_id>', true);
  SELECT set_config('app.user_id', '<user_id>', true);
  SELECT set_config('app.role', '<role>', true);

  ### ¿Por qué usamos RLS?

  Porque RLS agrega una segunda capa de seguridad directamente en PostgreSQL.

  Aunque un query del backend tenga un error, la base de datos filtra las filas permitidas según el tenant,
  usuario y rol.

  Ejemplo:

  - Un cliente solo ve sus cuentas.
  - Un admin ve los datos de su tenant.
  - Nadie ve datos de otro tenant.

  ———

  ## 11. Decisiones de Compatibilidad con EF Core

  Aunque el modelo usa aislamiento por tenant_id, se mantuvieron claves primarias simples:

  id UUID PRIMARY KEY

  Y se agregaron claves únicas compuestas:

  UNIQUE (tenant_id, id)

  ### ¿Por qué?

  EF Core trabaja más fácilmente con claves primarias simples. Pero para proteger el modelo multitenant se
  necesitan referencias compuestas.

  Esta combinación permite:

  - compatibilidad práctica con EF Core,
  - integridad fuerte por tenant,
  - menor complejidad en el backend.

  ———

  ## 12. Índices

  Se agregaron índices para mejorar consultas frecuentes:

  - usuarios por tenant y email,
  - cuentas por tenant y número de cuenta,
  - cuentas por propietario,
  - transacciones por tenant y fecha,
  - transacciones por tipo,
  - tasas de cambio por par de monedas,
  - idempotencia por clave y expiración.

  ### ¿Por qué?

  Porque el sistema necesita responder rápido en operaciones comunes como login, consulta de cuentas, historial
  paginado y validación de idempotencia.

  ———

  ## 13. Resumen de Seguridad

  La base de datos protege el sistema con varias capas:

  1. tenant_id en todas las tablas sensibles.
  2. Foreign keys compuestas para impedir cruces entre tenants.
  3. RLS para filtrar datos por sesión autenticada.
  4. Triggers para auditoría inmutable.
  5. Constraints para validar montos, monedas y estructura de transacciones.
  6. Idempotencia para evitar doble cobro.
  7. Soft delete para preservar historial financiero.

  ## Conclusión

  El diseño de base de datos de BankOS no se limita a almacenar información. También aplica reglas críticas de
  seguridad, aislamiento e integridad directamente en PostgreSQL.

  Esto reduce el riesgo de errores en el backend y cumple los requisitos principales del reto: multitenancy
  estricto, trazabilidad, operaciones financieras seguras, auditoría e idempotencia.