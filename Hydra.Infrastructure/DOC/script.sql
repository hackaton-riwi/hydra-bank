-- ===========================================================================
-- BANCO_OS - SCRIPT DE CREACIÓN V10
-- PostgreSQL + RLS + Multitenant + EF Core friendly
-- Idempotente: puede ejecutarse múltiples veces sin errores
-- ===========================================================================

-- ==========================================
-- 0. TEARDOWN — borrar todo en orden inverso
--    (dependencias primero, luego tablas base)
-- ==========================================

-- Funciones helper (DROP antes de recrear)
DROP FUNCTION IF EXISTS app_tenant_id()            CASCADE;
DROP FUNCTION IF EXISTS app_user_id()              CASCADE;
DROP FUNCTION IF EXISTS app_role()                 CASCADE;
DROP FUNCTION IF EXISTS fn_set_updated_at()        CASCADE;
DROP FUNCTION IF EXISTS fn_prevent_audit_mutation() CASCADE;

-- Tablas en orden inverso de dependencias
-- CASCADE elimina automáticamente FKs, índices, triggers y políticas
DROP TABLE IF EXISTS idempotency_records  CASCADE;
DROP TABLE IF EXISTS audit_logs           CASCADE;
DROP TABLE IF EXISTS transactions         CASCADE;
DROP TABLE IF EXISTS exchange_rates       CASCADE;
DROP TABLE IF EXISTS accounts             CASCADE;
DROP TABLE IF EXISTS users                CASCADE;
DROP TABLE IF EXISTS tenants              CASCADE;

-- Enums (deben borrarse después de las tablas que los usan)
DROP TYPE IF EXISTS idempotency_state  CASCADE;
DROP TYPE IF EXISTS fee_type_enum      CASCADE;
DROP TYPE IF EXISTS transaction_status CASCADE;
DROP TYPE IF EXISTS transaction_type   CASCADE;
DROP TYPE IF EXISTS account_status     CASCADE;
DROP TYPE IF EXISTS user_role          CASCADE;

-- ==========================================
-- 1. ENUMS
-- ==========================================

CREATE TYPE user_role          AS ENUM ('ADMIN', 'CLIENT');
CREATE TYPE account_status     AS ENUM ('ACTIVE', 'INACTIVE', 'BLOCKED');
CREATE TYPE transaction_type   AS ENUM ('DEPOSIT', 'WITHDRAW', 'TRANSFER');
CREATE TYPE transaction_status AS ENUM ('PENDING', 'SUCCESS', 'FAILED');
CREATE TYPE fee_type_enum      AS ENUM ('FIXED', 'PERCENTAGE');
CREATE TYPE idempotency_state  AS ENUM ('PROCESSING', 'COMPLETED', 'FAILED');

-- ==========================================
-- 2. TABLAS
-- ==========================================

CREATE TABLE tenants (
    id                     UUID          PRIMARY KEY,
    name                   VARCHAR(100)  NOT NULL,
    slug                   VARCHAR(50)   NOT NULL,
    main_currency          VARCHAR(3)    NOT NULL,
    max_transaction_amount NUMERIC(18,2) NOT NULL,
    fee_type               fee_type_enum NOT NULL,
    fee_value              NUMERIC(18,4) NOT NULL,
    webhook_url            TEXT,
    created_at             TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
    updated_at             TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_tenants_slug              UNIQUE (slug),
    CONSTRAINT chk_tenant_main_currency     CHECK (main_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT chk_max_amount               CHECK (max_transaction_amount > 0),
    CONSTRAINT chk_fee_value                CHECK (fee_value >= 0),
    CONSTRAINT chk_percentage_limit         CHECK (fee_type <> 'PERCENTAGE' OR fee_value <= 100)
);

CREATE TABLE users (
    id            UUID        PRIMARY KEY,
    tenant_id     UUID        NOT NULL,
    full_name     VARCHAR(150) NOT NULL,
    email         VARCHAR(150) NOT NULL,
    password_hash TEXT        NOT NULL,
    role          user_role   NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_users_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE RESTRICT,

    CONSTRAINT uq_users_tenant_id_id UNIQUE (tenant_id, id)
);

-- Email único por tenant (case-insensitive)
CREATE UNIQUE INDEX idx_users_tenant_lower_email
    ON users(tenant_id, lower(email));

CREATE TABLE accounts (
    id             UUID           PRIMARY KEY,
    tenant_id      UUID           NOT NULL,
    owner_id       UUID           NOT NULL,
    account_number VARCHAR(30)    NOT NULL,
    balance        NUMERIC(18,2)  NOT NULL DEFAULT 0,
    currency       VARCHAR(3)     NOT NULL,
    status         account_status NOT NULL DEFAULT 'ACTIVE',
    created_at     TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    deactivated_at TIMESTAMPTZ    NULL,

    CONSTRAINT fk_accounts_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE RESTRICT,

    -- Garantiza que el owner pertenezca al mismo tenant
    CONSTRAINT fk_accounts_owner_same_tenant
        FOREIGN KEY (tenant_id, owner_id)
        REFERENCES users(tenant_id, id)
        ON DELETE RESTRICT,

    CONSTRAINT uq_accounts_tenant_id_id     UNIQUE (tenant_id, id),
    CONSTRAINT chk_account_currency         CHECK (currency ~ '^[A-Z]{3}$'),
    CONSTRAINT chk_account_balance_positive CHECK (balance >= 0)
);

CREATE UNIQUE INDEX idx_accounts_tenant_account_number
    ON accounts(tenant_id, account_number);

CREATE INDEX idx_accounts_tenant_owner
    ON accounts(tenant_id, owner_id);

-- Cobertura para las subqueries de la policy transactions_select
CREATE INDEX idx_accounts_tenant_owner_cover
    ON accounts(tenant_id, owner_id, id);

CREATE TABLE exchange_rates (
    id            UUID          PRIMARY KEY,
    tenant_id     UUID          NOT NULL,
    from_currency VARCHAR(3)    NOT NULL,
    to_currency   VARCHAR(3)    NOT NULL,
    rate          NUMERIC(18,8) NOT NULL,
    created_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_exchange_rates_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE RESTRICT,

    CONSTRAINT chk_er_from                     CHECK (from_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT chk_er_to                       CHECK (to_currency ~ '^[A-Z]{3}$'),
    CONSTRAINT chk_exchange_rate_positive      CHECK (rate > 0),
    CONSTRAINT chk_exchange_different_currency CHECK (from_currency <> to_currency)
);

CREATE UNIQUE INDEX idx_exchange_rates_tenant_pair
    ON exchange_rates(tenant_id, from_currency, to_currency);

CREATE TABLE transactions (
    id                     UUID               PRIMARY KEY,
    tenant_id              UUID               NOT NULL,
    user_id                UUID               NOT NULL,
    type                   transaction_type   NOT NULL,
    source_account_id      UUID,
    destination_account_id UUID,
    original_amount        NUMERIC(18,2)      NOT NULL,
    converted_amount       NUMERIC(18,2),
    exchange_rate          NUMERIC(18,8),
    fee_amount             NUMERIC(18,2)      NOT NULL DEFAULT 0,
    status                 transaction_status NOT NULL DEFAULT 'PENDING',
    idempotency_key        UUID               NOT NULL,
    correlation_id         UUID               NOT NULL,
    created_at             TIMESTAMPTZ        NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_transactions_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_transactions_user_same_tenant
        FOREIGN KEY (tenant_id, user_id)
        REFERENCES users(tenant_id, id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_transactions_source_same_tenant
        FOREIGN KEY (tenant_id, source_account_id)
        REFERENCES accounts(tenant_id, id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED,

    CONSTRAINT fk_transactions_destination_same_tenant
        FOREIGN KEY (tenant_id, destination_account_id)
        REFERENCES accounts(tenant_id, id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED,

    CONSTRAINT uq_transactions_tenant_id_id UNIQUE (tenant_id, id),

    -- Seguridad si Redis cae: evita doble ejecución a nivel de BD
    CONSTRAINT uq_transactions_idempotency
        UNIQUE (tenant_id, user_id, idempotency_key),

    CONSTRAINT chk_trans_original_amount_positive
        CHECK (original_amount > 0),
    CONSTRAINT chk_trans_converted_amount_positive
        CHECK (converted_amount IS NULL OR converted_amount > 0),
    CONSTRAINT chk_trans_fee_positive
        CHECK (fee_amount >= 0),

    -- Valida la forma de cada tipo de transacción
    CONSTRAINT chk_transaction_account_shape CHECK (
        (type = 'DEPOSIT'
            AND source_account_id IS NULL
            AND destination_account_id IS NOT NULL)
        OR
        (type = 'WITHDRAW'
            AND source_account_id IS NOT NULL
            AND destination_account_id IS NULL)
        OR
        (type = 'TRANSFER'
            AND source_account_id IS NOT NULL
            AND destination_account_id IS NOT NULL
            AND source_account_id <> destination_account_id)
    )
);

CREATE INDEX idx_transactions_tenant_date
    ON transactions(tenant_id, created_at DESC);

CREATE INDEX idx_transactions_source
    ON transactions(source_account_id)
    WHERE source_account_id IS NOT NULL;

CREATE INDEX idx_transactions_destination
    ON transactions(destination_account_id)
    WHERE destination_account_id IS NOT NULL;

CREATE INDEX idx_transactions_correlation
    ON transactions(correlation_id);

CREATE INDEX idx_transactions_type
    ON transactions(tenant_id, type);

CREATE TABLE audit_logs (
    id         UUID         PRIMARY KEY,
    tenant_id  UUID         NOT NULL,
    user_id    UUID         NOT NULL,
    action     VARCHAR(100) NOT NULL,
    old_value  JSONB,
    new_value  JSONB,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_audit_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_audit_user_same_tenant
        FOREIGN KEY (tenant_id, user_id)
        REFERENCES users(tenant_id, id)
        ON DELETE RESTRICT
);

CREATE INDEX idx_audit_tenant_date
    ON audit_logs(tenant_id, created_at DESC);

CREATE TABLE idempotency_records (
    id              UUID              PRIMARY KEY,
    tenant_id       UUID              NOT NULL,
    user_id         UUID              NOT NULL,
    idempotency_key UUID              NOT NULL,
    request_hash    TEXT              NOT NULL,
    response_body   JSONB,
    status_code     INTEGER,
    state           idempotency_state NOT NULL DEFAULT 'PROCESSING',
    created_at      TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    expires_at      TIMESTAMPTZ       NOT NULL,

    CONSTRAINT uq_idempotency_key
        UNIQUE (tenant_id, user_id, idempotency_key),

    CONSTRAINT fk_idempotency_user_same_tenant
        FOREIGN KEY (tenant_id, user_id)
        REFERENCES users(tenant_id, id)
        ON DELETE RESTRICT,

    CONSTRAINT chk_idempotency_expiration
        CHECK (expires_at > created_at)
);

CREATE INDEX idx_idempotency_expiration
    ON idempotency_records(expires_at);

CREATE INDEX idx_idempotency_request_hash
    ON idempotency_records(tenant_id, user_id, request_hash);

-- ==========================================
-- 3. FUNCIONES HELPER (antes de triggers y policies)
-- ==========================================

CREATE FUNCTION app_tenant_id() RETURNS UUID AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::UUID;
$$ LANGUAGE sql STABLE;

CREATE FUNCTION app_user_id() RETURNS UUID AS $$
    SELECT NULLIF(current_setting('app.user_id', true), '')::UUID;
$$ LANGUAGE sql STABLE;

CREATE FUNCTION app_role() RETURNS TEXT AS $$
    SELECT current_setting('app.role', true);
$$ LANGUAGE sql STABLE;

-- ==========================================
-- 4. TRIGGERS
-- ==========================================

CREATE FUNCTION fn_set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_tenants_updated_at
    BEFORE UPDATE ON tenants
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

CREATE TRIGGER trg_users_updated_at
    BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

CREATE TRIGGER trg_accounts_updated_at
    BEFORE UPDATE ON accounts
    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();

CREATE FUNCTION fn_prevent_audit_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Operacion denegada: audit_logs es append-only.';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_protect_audit_logs
    BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION fn_prevent_audit_mutation();

-- ==========================================
-- 5. ROW LEVEL SECURITY
-- ==========================================

ALTER TABLE tenants             ENABLE ROW LEVEL SECURITY;
ALTER TABLE users               ENABLE ROW LEVEL SECURITY;
ALTER TABLE accounts            ENABLE ROW LEVEL SECURITY;
ALTER TABLE exchange_rates      ENABLE ROW LEVEL SECURITY;
ALTER TABLE transactions        ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_logs          ENABLE ROW LEVEL SECURITY;
ALTER TABLE idempotency_records ENABLE ROW LEVEL SECURITY;

-- ==========================================
-- POLICIES: SELECT
-- ==========================================

CREATE POLICY tenants_select ON tenants
    AS PERMISSIVE FOR SELECT
    USING (id = app_tenant_id());

CREATE POLICY users_select ON users
    AS PERMISSIVE FOR SELECT
    USING (
        tenant_id = app_tenant_id()
        AND (
            app_role() = 'ADMIN'
            OR id = app_user_id()
        )
    );

CREATE POLICY accounts_select ON accounts
    AS PERMISSIVE FOR SELECT
    USING (
        tenant_id = app_tenant_id()
        AND (
            app_role() = 'ADMIN'
            OR owner_id = app_user_id()
        )
    );

CREATE POLICY exchange_rates_select ON exchange_rates
    AS PERMISSIVE FOR SELECT
    USING (tenant_id = app_tenant_id());

CREATE POLICY transactions_select ON transactions
    AS PERMISSIVE FOR SELECT
    USING (
        tenant_id = app_tenant_id()
        AND (
            app_role() = 'ADMIN'
            OR user_id = app_user_id()
            OR source_account_id IN (
                SELECT id FROM accounts
                WHERE tenant_id = app_tenant_id()
                  AND owner_id  = app_user_id()
            )
            OR destination_account_id IN (
                SELECT id FROM accounts
                WHERE tenant_id = app_tenant_id()
                  AND owner_id  = app_user_id()
            )
        )
    );

CREATE POLICY audit_logs_select ON audit_logs
    AS PERMISSIVE FOR SELECT
    USING (
        tenant_id = app_tenant_id()
        AND app_role() = 'ADMIN'
    );

CREATE POLICY idempotency_select ON idempotency_records
    AS PERMISSIVE FOR SELECT
    USING (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    );

-- ==========================================
-- POLICIES: INSERT
-- ==========================================

-- Solo el service role del backend puede registrar tenants nuevos
CREATE POLICY tenants_insert_system ON tenants
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        current_setting('app.is_system', true) = 'true'
    );

CREATE POLICY users_insert_admin ON users
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND app_role() = 'ADMIN'
    );

CREATE POLICY accounts_insert_admin ON accounts
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND app_role() = 'ADMIN'
    );

CREATE POLICY exchange_rates_insert_admin ON exchange_rates
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND app_role() = 'ADMIN'
    );

CREATE POLICY transactions_insert_authenticated ON transactions
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    );

CREATE POLICY audit_logs_insert_authenticated ON audit_logs
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND (
            user_id = app_user_id()
            OR app_role() = 'ADMIN'
        )
    );

CREATE POLICY idempotency_insert_own ON idempotency_records
    AS PERMISSIVE FOR INSERT
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    );

-- ==========================================
-- POLICIES: UPDATE
-- ==========================================

CREATE POLICY tenants_update_admin ON tenants
    AS PERMISSIVE FOR UPDATE
    USING    (id = app_tenant_id() AND app_role() = 'ADMIN')
    WITH CHECK (id = app_tenant_id() AND app_role() = 'ADMIN');

CREATE POLICY users_update_admin ON users
    AS PERMISSIVE FOR UPDATE
    USING    (tenant_id = app_tenant_id() AND app_role() = 'ADMIN')
    WITH CHECK (tenant_id = app_tenant_id() AND app_role() = 'ADMIN');

CREATE POLICY accounts_update_admin ON accounts
    AS PERMISSIVE FOR UPDATE
    USING    (tenant_id = app_tenant_id() AND app_role() = 'ADMIN')
    WITH CHECK (tenant_id = app_tenant_id() AND app_role() = 'ADMIN');

CREATE POLICY exchange_rates_update_admin ON exchange_rates
    AS PERMISSIVE FOR UPDATE
    USING    (tenant_id = app_tenant_id() AND app_role() = 'ADMIN')
    WITH CHECK (tenant_id = app_tenant_id() AND app_role() = 'ADMIN');

-- Solo el backend transiciona PENDING → SUCCESS / FAILED
CREATE POLICY transactions_update_backend ON transactions
    AS PERMISSIVE FOR UPDATE
    USING (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    )
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    );

-- El backend transiciona PROCESSING → COMPLETED / FAILED
CREATE POLICY idempotency_update_backend ON idempotency_records
    AS PERMISSIVE FOR UPDATE
    USING (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    )
    WITH CHECK (
        tenant_id = app_tenant_id()
        AND user_id = app_user_id()
    );

-- ===========================================================================
-- FIN DEL SCRIPT
-- ===========================================================================