using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hydra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExchangeRatesCopOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropCheckConstraint(
                name: "chk_trans_converted_amount_positive",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "converted_amount",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "exchange_rate",
                table: "transactions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "converted_amount",
                table: "transactions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "exchange_rate",
                table: "transactions",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    from_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    to_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("exchange_rates_pkey", x => x.id);
                    table.CheckConstraint("chk_er_from", "from_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("chk_er_to", "to_currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("chk_exchange_different_currency", "from_currency <> to_currency");
                    table.CheckConstraint("chk_exchange_rate_positive", "rate > 0");
                    table.ForeignKey(
                        name: "exchange_rates_tenant_id_fkey",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_trans_converted_amount_positive",
                table: "transactions",
                sql: "converted_amount IS NULL OR converted_amount > 0");

            migrationBuilder.CreateIndex(
                name: "idx_exchange_rates_tenant_pair",
                table: "exchange_rates",
                columns: new[] { "tenant_id", "from_currency", "to_currency" },
                unique: true);
        }
    }
}
