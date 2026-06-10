  -- ===========================================================================
  -- BANCO_OS - SCRIPT ABSOLUTO DE CREACIÓN V8 FINAL LIMPIO
  -- PostgreSQL + RLS + Multitenant + EF Core friendly
  -- ===========================================================================

  -- ==========================================
  -- 1. ENUMS
  -- ==========================================
  DO $$
  BEGIN
      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'user_role') THEN
          CREATE TYPE user_role AS ENUM ('ADMIN', 'CLIENT');
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'account_status') THEN
          CREATE TYPE account_status AS ENUM ('ACTIVE', 'INACTIVE', 'BLOCKED');
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'transaction_type') THEN
          CREATE TYPE transaction_type AS ENUM ('DEPOSIT', 'WITHDRAW', 'TRANSFER');
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'transaction_status') THEN
          CREATE TYPE transaction_status AS ENUM ('PENDING', 'SUCCESS', 'FAILED');
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'fee_type_enum') THEN
          CREATE TYPE fee_type_enum AS ENUM ('FIXED', 'PERCENTAGE');
      END IF;

      IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'idempotency_state') THEN
          CREATE TYPE idempotency_state AS ENUM ('PROCESSING', 'COMPLETED');
      END IF;
  END $$;

  -- ==========================================
  -- 2. TABLES
  -- ==========================================

  CREATE TABLE IF NOT EXISTS tenants (
      id UUID PRIMARY KEY,
      name VARCHAR(100) NOT NULL,
      slug VARCHAR(50) UNIQUE NOT NULL,
      main_currency VARCHAR(3) NOT NULL CONSTRAINT chk_tenant_main_currency CHECK (main_currency ~ '^[A-Z]
      {3}$'),
      max_transaction_amount NUMERIC(18,2) NOT NULL CONSTRAINT chk_max_amount CHECK (max_transaction_amount >
      0),
      fee_type fee_type_enum NOT NULL,
      fee_value NUMERIC(18,4) NOT NULL CONSTRAINT chk_fee_value CHECK (fee_value >= 0),
      webhook_url TEXT,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      CONSTRAINT chk_percentage_limit CHECK (fee_type <> 'PERCENTAGE' OR fee_value <= 100)
  );

  CREATE TABLE IF NOT EXISTS users (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
      full_name VARCHAR(150) NOT NULL,
      email VARCHAR(150) NOT NULL,
      password_hash TEXT NOT NULL,
      role user_role NOT NULL,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      CONSTRAINT uq_users_tenant_id_id UNIQUE (tenant_id, id)
  );

  CREATE UNIQUE INDEX IF NOT EXISTS idx_users_tenant_lower_email
  ON users(tenant_id, lower(email));

  CREATE TABLE IF NOT EXISTS accounts (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL,
      owner_id UUID NOT NULL,
      account_number VARCHAR(30) NOT NULL,
      balance NUMERIC(18,2) NOT NULL DEFAULT 0,
      currency VARCHAR(3) NOT NULL CONSTRAINT chk_account_currency CHECK (currency ~ '^[A-Z]{3}$'),
      status account_status NOT NULL DEFAULT 'ACTIVE',
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      deactivated_at TIMESTAMPTZ NULL,

      CONSTRAINT uq_accounts_tenant_id_id UNIQUE (tenant_id, id),
      CONSTRAINT chk_account_balance_positive CHECK (balance >= 0),
      CONSTRAINT fk_accounts_owner_same_tenant
          FOREIGN KEY (tenant_id, owner_id)
          REFERENCES users(tenant_id, id)
          ON DELETE RESTRICT
  );

  CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_tenant_account_number
  ON accounts(tenant_id, account_number);

  CREATE INDEX IF NOT EXISTS idx_accounts_tenant_owner
  ON accounts(tenant_id, owner_id);

  CREATE TABLE IF NOT EXISTS exchange_rates (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
      from_currency VARCHAR(3) NOT NULL CONSTRAINT chk_er_from CHECK (from_currency ~ '^[A-Z]{3}$'),
      to_currency VARCHAR(3) NOT NULL CONSTRAINT chk_er_to CHECK (to_currency ~ '^[A-Z]{3}$'),
      rate NUMERIC(18,8) NOT NULL,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

      CONSTRAINT chk_exchange_rate_positive CHECK (rate > 0),
      CONSTRAINT chk_exchange_different_currency CHECK (from_currency <> to_currency)
  );

  CREATE UNIQUE INDEX IF NOT EXISTS idx_exchange_rates_tenant_pair
  ON exchange_rates(tenant_id, from_currency, to_currency);

  CREATE TABLE IF NOT EXISTS transactions (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL,
      user_id UUID NOT NULL,
      type transaction_type NOT NULL,
      source_account_id UUID,
      destination_account_id UUID,
      original_amount NUMERIC(18,2) NOT NULL,
      converted_amount NUMERIC(18,2),
      exchange_rate NUMERIC(18,8),
      fee_amount NUMERIC(18,2) DEFAULT 0,
      status transaction_status NOT NULL DEFAULT 'PENDING',
      idempotency_key UUID NOT NULL,
      correlation_id UUID NOT NULL,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

      CONSTRAINT uq_transactions_tenant_id_id UNIQUE (tenant_id, id),
      CONSTRAINT chk_trans_original_amount_positive CHECK (original_amount > 0),
      CONSTRAINT chk_trans_converted_amount_positive CHECK (converted_amount IS NULL OR converted_amount > 0),
      CONSTRAINT chk_trans_fee_positive CHECK (fee_amount >= 0),
      CONSTRAINT chk_transaction_account_shape CHECK (
          (type = 'DEPOSIT' AND source_account_id IS NULL AND destination_account_id IS NOT NULL)
          OR
          (type = 'WITHDRAW' AND source_account_id IS NOT NULL AND destination_account_id IS NULL)
          OR
          (type = 'TRANSFER' AND source_account_id IS NOT NULL AND destination_account_id IS NOT NULL AND
          source_account_id <> destination_account_id)
      ),

      CONSTRAINT fk_transactions_user_same_tenant
          FOREIGN KEY (tenant_id, user_id)
          REFERENCES users(tenant_id, id)
          ON DELETE RESTRICT,

      CONSTRAINT fk_transactions_source_same_tenant
          FOREIGN KEY (tenant_id, source_account_id)
          REFERENCES accounts(tenant_id, id)
          ON DELETE RESTRICT,

      CONSTRAINT fk_transactions_destination_same_tenant
          FOREIGN KEY (tenant_id, destination_account_id)
          REFERENCES accounts(tenant_id, id)
          ON DELETE RESTRICT
  );

  CREATE INDEX IF NOT EXISTS idx_transactions_tenant_date
  ON transactions(tenant_id, created_at DESC);

  CREATE INDEX IF NOT EXISTS idx_transactions_source
  ON transactions(source_account_id)
  WHERE source_account_id IS NOT NULL;

  CREATE INDEX IF NOT EXISTS idx_transactions_destination
  ON transactions(destination_account_id)
  WHERE destination_account_id IS NOT NULL;

  CREATE INDEX IF NOT EXISTS idx_transactions_correlation
  ON transactions(correlation_id);

  CREATE INDEX IF NOT EXISTS idx_transactions_type
  ON transactions(type);

  CREATE TABLE IF NOT EXISTS audit_logs (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL,
      user_id UUID NOT NULL,
      action VARCHAR(100) NOT NULL,
      old_value JSONB,
      new_value JSONB,
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

      CONSTRAINT fk_audit_user_same_tenant
          FOREIGN KEY (tenant_id, user_id)
          REFERENCES users(tenant_id, id)
          ON DELETE RESTRICT
  );

  CREATE INDEX IF NOT EXISTS idx_audit_tenant_date
  ON audit_logs(tenant_id, created_at DESC);

  CREATE TABLE IF NOT EXISTS idempotency_records (
      id UUID PRIMARY KEY,
      tenant_id UUID NOT NULL,
      user_id UUID NOT NULL,
      idempotency_key UUID NOT NULL,
      request_hash TEXT NOT NULL,
      response_body JSONB,
      status_code INTEGER,
      state idempotency_state NOT NULL DEFAULT 'PROCESSING',
      created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      expires_at TIMESTAMPTZ NOT NULL,

      UNIQUE (tenant_id, user_id, idempotency_key),
      CONSTRAINT chk_idempotency_expiration CHECK (expires_at > created_at),

      CONSTRAINT fk_idempotency_user_same_tenant
          FOREIGN KEY (tenant_id, user_id)
          REFERENCES users(tenant_id, id)
          ON DELETE RESTRICT
  );

  CREATE INDEX IF NOT EXISTS idx_idempotency_key
  ON idempotency_records(idempotency_key);

  CREATE INDEX IF NOT EXISTS idx_idempotency_expiration
  ON idempotency_records(expires_at);

  -- ==========================================
  -- 3. AUDIT LOG IMMUTABILITY
  -- ==========================================

  CREATE OR REPLACE FUNCTION prevent_audit_mutation()
  RETURNS TRIGGER AS $$
  BEGIN
      RAISE EXCEPTION 'Operacion denegada: la tabla audit_logs es estrictamente append-only.';
  END;
  $$ LANGUAGE plpgsql;

  DO $$
  BEGIN
      IF NOT EXISTS (
          SELECT 1
          FROM pg_trigger
          WHERE tgname = 'trg_protect_audit_logs'
      ) THEN
          CREATE TRIGGER trg_protect_audit_logs
          BEFORE UPDATE OR DELETE ON audit_logs
          FOR EACH ROW
          EXECUTE FUNCTION prevent_audit_mutation();
      END IF;
  END $$;

  -- ==========================================
  -- 4. RLS
  -- ==========================================

  ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
  ALTER TABLE users ENABLE ROW LEVEL SECURITY;
  ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
  ALTER TABLE exchange_rates ENABLE ROW LEVEL SECURITY;
  ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;
  ALTER TABLE audit_logs ENABLE ROW LEVEL SECURITY;
  ALTER TABLE idempotency_records ENABLE ROW LEVEL SECURITY;

  -- ==========================================
  -- SELECT POLICIES
  -- ==========================================

  DROP POLICY IF EXISTS tenants_select ON tenants;
  CREATE POLICY tenants_select ON tenants AS PERMISSIVE FOR SELECT
  USING (
      id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
  );

  DROP POLICY IF EXISTS users_select ON users;
  CREATE POLICY users_select ON users AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND (
          current_setting('app.role', true) = 'ADMIN'
          OR id = NULLIF(current_setting('app.user_id', true), '')::uuid
      )
  );

  DROP POLICY IF EXISTS accounts_select ON accounts;
  CREATE POLICY accounts_select ON accounts AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND (
          current_setting('app.role', true) = 'ADMIN'
          OR owner_id = NULLIF(current_setting('app.user_id', true), '')::uuid
      )
  );

  DROP POLICY IF EXISTS exchange_rates_select ON exchange_rates;
  CREATE POLICY exchange_rates_select ON exchange_rates AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
  );

  DROP POLICY IF EXISTS transactions_select ON transactions;
  CREATE POLICY transactions_select ON transactions AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND (
          current_setting('app.role', true) = 'ADMIN'
          OR user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
          OR source_account_id IN (
              SELECT id
              FROM accounts
              WHERE tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND owner_id = NULLIF(current_setting('app.user_id', true), '')::uuid
          )
          OR destination_account_id IN (
              SELECT id
              FROM accounts
              WHERE tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND owner_id = NULLIF(current_setting('app.user_id', true), '')::uuid
          )
      )
  );

  DROP POLICY IF EXISTS audit_logs_select ON audit_logs;
  CREATE POLICY audit_logs_select ON audit_logs AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS idempotency_select ON idempotency_records;
  CREATE POLICY idempotency_select ON idempotency_records AS PERMISSIVE FOR SELECT
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  );

  -- ==========================================
  -- INSERT POLICIES
  -- ==========================================

  DROP POLICY IF EXISTS tenants_insert_admin ON tenants;
  CREATE POLICY tenants_insert_admin ON tenants AS PERMISSIVE FOR INSERT
  WITH CHECK (
      current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS users_insert_admin ON users;
  CREATE POLICY users_insert_admin ON users AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS accounts_insert_admin ON accounts;
  CREATE POLICY accounts_insert_admin ON accounts AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS exchange_rates_insert_admin ON exchange_rates;
  CREATE POLICY exchange_rates_insert_admin ON exchange_rates AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS transactions_insert_client_or_admin ON transactions;
  CREATE POLICY transactions_insert_client_or_admin ON transactions AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  );

  DROP POLICY IF EXISTS audit_logs_insert_authenticated ON audit_logs;
  CREATE POLICY audit_logs_insert_authenticated ON audit_logs AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND (
          user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
          OR current_setting('app.role', true) = 'ADMIN'
      )
  );

  DROP POLICY IF EXISTS idempotency_insert_own ON idempotency_records;
  CREATE POLICY idempotency_insert_own ON idempotency_records AS PERMISSIVE FOR INSERT
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  );

  -- ==========================================
  -- UPDATE POLICIES
  -- ==========================================

  DROP POLICY IF EXISTS tenants_update_admin ON tenants;
  CREATE POLICY tenants_update_admin ON tenants AS PERMISSIVE FOR UPDATE
  USING (
      id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  )
  WITH CHECK (
      id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS users_update_admin ON users;
  CREATE POLICY users_update_admin ON users AS PERMISSIVE FOR UPDATE
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  )
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS accounts_update_admin ON accounts;
  CREATE POLICY accounts_update_admin ON accounts AS PERMISSIVE FOR UPDATE
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  )
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS exchange_rates_update_admin ON exchange_rates;
  CREATE POLICY exchange_rates_update_admin ON exchange_rates AS PERMISSIVE FOR UPDATE
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  )
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND current_setting('app.role', true) = 'ADMIN'
  );

  DROP POLICY IF EXISTS transactions_update_backend ON transactions;
  CREATE POLICY transactions_update_backend ON transactions AS PERMISSIVE FOR UPDATE
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  )
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  );

  DROP POLICY IF EXISTS idempotency_update_backend ON idempotency_records;
  CREATE POLICY idempotency_update_backend ON idempotency_records AS PERMISSIVE FOR UPDATE
  USING (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  )
  WITH CHECK (
      tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
      AND user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
  );