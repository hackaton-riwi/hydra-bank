using System;
using Hydra.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hydra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'idempotency_state') THEN
                        CREATE TYPE idempotency_state AS ENUM ('PROCESSING', 'COMPLETED', 'FAILED');
                    ELSE
                        ALTER TYPE idempotency_state ADD VALUE IF NOT EXISTS 'FAILED';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<IdempotencyState>(type: "idempotency_state", nullable: false, defaultValueSql: "'PROCESSING'::idempotency_state"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("idempotency_records_pkey", x => x.id);
                    table.CheckConstraint("chk_idempotency_expiration", "expires_at > created_at");
                    table.ForeignKey(
                        name: "fk_idempotency_user_same_tenant",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idempotency_records_tenant_id_user_id_idempotency_key_key",
                table: "idempotency_records",
                columns: new[] { "tenant_id", "user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_idempotency_expiration",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "idx_idempotency_key",
                table: "idempotency_records",
                column: "idempotency_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");
        }
    }
}
