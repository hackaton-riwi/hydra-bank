using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hydra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AccountDocumentAndRecharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "transactions");

            migrationBuilder.DropIndex(
                name: "idx_accounts_tenant_owner",
                table: "accounts");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:account_status", "ACTIVE,INACTIVE,BLOCKED")
                .Annotation("Npgsql:Enum:fee_type_enum", "FIXED,PERCENTAGE")
                .Annotation("Npgsql:Enum:transaction_status", "PENDING,SUCCESS,FAILED")
                .Annotation("Npgsql:Enum:transaction_type", "DEPOSIT,WITHDRAW,TRANSFER")
                .Annotation("Npgsql:Enum:user_role", "ADMIN,CLIENT")
                .OldAnnotation("Npgsql:Enum:account_status", "ACTIVE,INACTIVE,BLOCKED")
                .OldAnnotation("Npgsql:Enum:fee_type_enum", "FIXED,PERCENTAGE")
                .OldAnnotation("Npgsql:Enum:idempotency_state", "PROCESSING,COMPLETED")
                .OldAnnotation("Npgsql:Enum:transaction_status", "PENDING,SUCCESS,FAILED")
                .OldAnnotation("Npgsql:Enum:transaction_type", "DEPOSIT,WITHDRAW,TRANSFER")
                .OldAnnotation("Npgsql:Enum:user_role", "ADMIN,CLIENT");

            migrationBuilder.AddColumn<string>(
                name: "document_number",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE users
                SET document_number = id::text
                WHERE document_number IS NULL OR document_number = ''
                """);

            migrationBuilder.AlterColumn<string>(
                name: "document_number",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "uq_users_tenant_document_number",
                table: "users",
                columns: new[] { "tenant_id", "document_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_accounts_tenant_owner",
                table: "accounts",
                columns: new[] { "tenant_id", "owner_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_users_tenant_document_number",
                table: "users");

            migrationBuilder.DropColumn(
                name: "document_number",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_accounts_tenant_owner",
                table: "accounts");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:account_status", "ACTIVE,INACTIVE,BLOCKED")
                .Annotation("Npgsql:Enum:fee_type_enum", "FIXED,PERCENTAGE")
                .Annotation("Npgsql:Enum:idempotency_state", "PROCESSING,COMPLETED")
                .Annotation("Npgsql:Enum:transaction_status", "PENDING,SUCCESS,FAILED")
                .Annotation("Npgsql:Enum:transaction_type", "DEPOSIT,WITHDRAW,TRANSFER")
                .Annotation("Npgsql:Enum:user_role", "ADMIN,CLIENT")
                .OldAnnotation("Npgsql:Enum:account_status", "ACTIVE,INACTIVE,BLOCKED")
                .OldAnnotation("Npgsql:Enum:fee_type_enum", "FIXED,PERCENTAGE")
                .OldAnnotation("Npgsql:Enum:transaction_status", "PENDING,SUCCESS,FAILED")
                .OldAnnotation("Npgsql:Enum:transaction_type", "DEPOSIT,WITHDRAW,TRANSFER")
                .OldAnnotation("Npgsql:Enum:user_role", "ADMIN,CLIENT");

            migrationBuilder.AddColumn<Guid>(
                name: "idempotency_key",
                table: "transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "idx_accounts_tenant_owner",
                table: "accounts",
                columns: new[] { "tenant_id", "owner_id" });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<int>(type: "idempotency_state", nullable: false, defaultValueSql: "'PROCESSING'::idempotency_state"),
                    status_code = table.Column<int>(type: "integer", nullable: true)
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
    }
}
