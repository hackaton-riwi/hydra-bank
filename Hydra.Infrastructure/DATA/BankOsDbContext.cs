using System;
using System.Collections.Generic;
using Hydra.Domain.Enums;
using Hydra.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hydra.Infrastructure.DATA;

public partial class BankOsDbContext : IdentityDbContext
{
    public BankOsDbContext(DbContextOptions<BankOsDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Account> Accounts { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<ExchangeRate> ExchangeRates { get; set; }

    public virtual DbSet<IdempotencyRecord> IdempotencyRecords { get; set; }

    public virtual DbSet<Tenant> Tenants { get; set; }

    public virtual DbSet<Transaction> Transactions { get; set; }

    public virtual DbSet<User> BankUsers { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder
            .HasPostgresEnum("account_status", new[] { "ACTIVE", "INACTIVE", "BLOCKED" })
            .HasPostgresEnum("fee_type_enum", new[] { "FIXED", "PERCENTAGE" })
            .HasPostgresEnum("idempotency_state", new[] { "PROCESSING", "COMPLETED" })
            .HasPostgresEnum("transaction_status", new[] { "PENDING", "SUCCESS", "FAILED" })
            .HasPostgresEnum("transaction_type", new[] { "DEPOSIT", "WITHDRAW", "TRANSFER" })
            .HasPostgresEnum("user_role", new[] { "ADMIN", "CLIENT" });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("accounts_pkey");

            entity.ToTable("accounts");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("chk_account_balance_positive", "balance >= 0");
                t.HasCheckConstraint("chk_account_currency", "currency ~ '^[A-Z]{3}$'");
            });

            entity.HasIndex(e => new { e.TenantId, e.AccountNumber }, "idx_accounts_tenant_account_number").IsUnique();

            entity.HasIndex(e => new { e.TenantId, e.OwnerId }, "idx_accounts_tenant_owner");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_accounts_tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.AccountNumber)
                .HasMaxLength(30)
                .HasColumnName("account_number");
            entity.Property(e => e.Balance)
                .HasPrecision(18, 2)
                .HasColumnName("balance");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasColumnName("currency");
            entity.Property(e => e.DeactivatedAt).HasColumnName("deactivated_at");
            entity.Property(e => e.OwnerId).HasColumnName("owner_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'ACTIVE'::account_status")
                .HasColumnName("status")
                .HasColumnType("account_status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.User).WithMany(p => p.Accounts)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.OwnerId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_accounts_owner_same_tenant");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("audit_logs_pkey");

            entity.ToTable("audit_logs");

            entity.HasIndex(e => new { e.TenantId, e.CreatedAt }, "idx_audit_tenant_date").IsDescending(false, true);

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.Action)
                .HasMaxLength(100)
                .HasColumnName("action");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.NewValue)
                .HasColumnType("jsonb")
                .HasColumnName("new_value");
            entity.Property(e => e.OldValue)
                .HasColumnType("jsonb")
                .HasColumnName("old_value");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.AuditLogs)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_audit_user_same_tenant");
        });

        modelBuilder.Entity<ExchangeRate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("exchange_rates_pkey");

            entity.ToTable("exchange_rates");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("chk_er_from", "from_currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("chk_er_to", "to_currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("chk_exchange_different_currency", "from_currency <> to_currency");
                t.HasCheckConstraint("chk_exchange_rate_positive", "rate > 0");
            });

            entity.HasIndex(e => new { e.TenantId, e.FromCurrency, e.ToCurrency }, "idx_exchange_rates_tenant_pair").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FromCurrency)
                .HasMaxLength(3)
                .HasColumnName("from_currency");
            entity.Property(e => e.Rate)
                .HasPrecision(18, 8)
                .HasColumnName("rate");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.ToCurrency)
                .HasMaxLength(3)
                .HasColumnName("to_currency");

            entity.HasOne(d => d.Tenant).WithMany(p => p.ExchangeRates)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("exchange_rates_tenant_id_fkey");
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("idempotency_records_pkey");

            entity.ToTable("idempotency_records");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("chk_idempotency_expiration", "expires_at > created_at");
            });

            entity.HasIndex(e => new { e.TenantId, e.UserId, e.IdempotencyKey }, "idempotency_records_tenant_id_user_id_idempotency_key_key").IsUnique();

            entity.HasIndex(e => e.ExpiresAt, "idx_idempotency_expiration");

            entity.HasIndex(e => e.IdempotencyKey, "idx_idempotency_key");

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.RequestHash).HasColumnName("request_hash");
            entity.Property(e => e.ResponseBody)
                .HasColumnType("jsonb")
                .HasColumnName("response_body");
            entity.Property(e => e.State)
                .HasDefaultValueSql("'PROCESSING'::idempotency_state")
                .HasColumnName("state")
                .HasColumnType("idempotency_state");
            entity.Property(e => e.StatusCode).HasColumnName("status_code");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.IdempotencyRecords)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_idempotency_user_same_tenant");
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("tenants_pkey");

            entity.ToTable("tenants");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("chk_fee_value", "fee_value >= 0");
                t.HasCheckConstraint("chk_max_amount", "max_transaction_amount > 0");
                t.HasCheckConstraint("chk_percentage_limit", "fee_type <> 'PERCENTAGE' OR fee_value <= 100");
                t.HasCheckConstraint("chk_tenant_main_currency", "main_currency ~ '^[A-Z]{3}$'");
            });

            entity.HasIndex(e => e.Slug, "tenants_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FeeType)
                .HasColumnName("fee_type")
                .HasColumnType("fee_type_enum");
            entity.Property(e => e.FeeValue)
                .HasPrecision(18, 4)
                .HasColumnName("fee_value");
            entity.Property(e => e.MainCurrency)
                .HasMaxLength(3)
                .HasColumnName("main_currency");
            entity.Property(e => e.MaxTransactionAmount)
                .HasPrecision(18, 2)
                .HasColumnName("max_transaction_amount");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasColumnName("name");
            entity.Property(e => e.Slug)
                .HasMaxLength(50)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.WebhookUrl).HasColumnName("webhook_url");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("transactions_pkey");

            entity.ToTable("transactions");

            entity.HasIndex(e => e.CorrelationId, "idx_transactions_correlation");

            entity.HasIndex(e => e.DestinationAccountId, "idx_transactions_destination").HasFilter("(destination_account_id IS NOT NULL)");

            entity.HasIndex(e => e.SourceAccountId, "idx_transactions_source").HasFilter("(source_account_id IS NOT NULL)");

            entity.HasIndex(e => new { e.TenantId, e.CreatedAt }, "idx_transactions_tenant_date").IsDescending(false, true);

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_transactions_tenant_id_id").IsUnique();

            entity.HasIndex(e => e.Type, "idx_transactions_type");

            entity.ToTable(t =>
            {
                t.HasCheckConstraint("chk_trans_converted_amount_positive", "converted_amount IS NULL OR converted_amount > 0");
                t.HasCheckConstraint("chk_trans_fee_positive", "fee_amount >= 0");
                t.HasCheckConstraint("chk_trans_original_amount_positive", "original_amount > 0");
                t.HasCheckConstraint("chk_transaction_account_shape", """
                    (type = 'DEPOSIT' AND source_account_id IS NULL AND destination_account_id IS NOT NULL)
                    OR (type = 'WITHDRAW' AND source_account_id IS NOT NULL AND destination_account_id IS NULL)
                    OR (type = 'TRANSFER' AND source_account_id IS NOT NULL AND destination_account_id IS NOT NULL AND source_account_id <> destination_account_id)
                    """);
            });

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.ConvertedAmount)
                .HasPrecision(18, 2)
                .HasColumnName("converted_amount");
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DestinationAccountId).HasColumnName("destination_account_id");
            entity.Property(e => e.ExchangeRate)
                .HasPrecision(18, 8)
                .HasColumnName("exchange_rate");
            entity.Property(e => e.FeeAmount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("fee_amount");
            entity.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(e => e.OriginalAmount)
                .HasPrecision(18, 2)
                .HasColumnName("original_amount");
            entity.Property(e => e.SourceAccountId).HasColumnName("source_account_id");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'PENDING'::transaction_status")
                .HasColumnName("status")
                .HasColumnType("transaction_status");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasColumnType("transaction_type");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.Account).WithMany(p => p.TransactionAccounts)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.DestinationAccountId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_transactions_destination_same_tenant");

            entity.HasOne(d => d.AccountNavigation).WithMany(p => p.TransactionAccountNavigations)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.SourceAccountId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_transactions_source_same_tenant");

            entity.HasOne(d => d.User).WithMany(p => p.Transactions)
                .HasPrincipalKey(p => new { p.TenantId, p.Id })
                .HasForeignKey(d => new { d.TenantId, d.UserId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_transactions_user_same_tenant");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users");

            entity.HasIndex(e => new { e.TenantId, e.Id }, "uq_users_tenant_id_id").IsUnique();

            entity.Property(e => e.Id)
                .ValueGeneratedNever()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Email)
                .HasMaxLength(150)
                .HasColumnName("email");
            entity.Property(e => e.FullName)
                .HasMaxLength(150)
                .HasColumnName("full_name");
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash");
            entity.Property(e => e.Role)
                .HasColumnName("role")
                .HasColumnType("user_role");
            entity.Property(e => e.TenantId).HasColumnName("tenant_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Tenant).WithMany(p => p.Users)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("users_tenant_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
