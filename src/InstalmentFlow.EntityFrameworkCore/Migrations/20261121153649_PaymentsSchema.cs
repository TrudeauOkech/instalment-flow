using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InstalmentFlow.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    internal partial class PaymentsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "intent_checks",
                schema: "instalment_flow",
                columns: table => new
                {
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intent_checks", x => x.intent_id);
                });

            migrationBuilder.CreateTable(
                name: "payment_intents",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account = table.Column<string>(type: "text", nullable: false),
                    payer = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<short>(type: "smallint", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_reference = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    transaction_id = table.Column<string>(type: "text", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_checks = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_intents", x => new { x.tenant_id, x.intent_id });
                    table.CheckConstraint("ck_payment_intents_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_payment_intents_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_inbox",
                schema: "instalment_flow",
                columns: table => new
                {
                    inbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    dedupe_key = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    @event = table.Column<string>(name: "event", type: "jsonb", nullable: false),
                    raw_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<string>(type: "text", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_inbox", x => x.inbox_id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_discrepancies",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    discrepancy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    transaction_id = table.Column<string>(type: "text", nullable: false),
                    statement_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    engine_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_discrepancies", x => new { x.tenant_id, x.discrepancy_id });
                    table.ForeignKey(
                        name: "fk_reconciliation_discrepancies_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                schema: "instalment_flow",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account = table.Column<string>(type: "text", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    missing_in_engine = table.Column<int>(type: "integer", nullable: false),
                    missing_at_provider = table.Column<int>(type: "integer", nullable: false),
                    amount_mismatches = table.Column<int>(type: "integer", nullable: false),
                    reversed_at_provider = table.Column<int>(type: "integer", nullable: false),
                    unroutable = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_provider_accounts",
                schema: "instalment_flow",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_provider_accounts", x => new { x.provider, x.provider_account });
                    table.ForeignKey(
                        name: "fk_tenant_provider_accounts_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intent_checks_next_check_at",
                schema: "instalment_flow",
                table: "intent_checks",
                column: "next_check_at");

            migrationBuilder.CreateIndex(
                name: "ux_payment_intents_correlation",
                schema: "instalment_flow",
                table: "payment_intents",
                columns: new[] { "tenant_id", "provider", "correlation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_payment_intents_key",
                schema: "instalment_flow",
                table: "payment_intents",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_inbox_pending",
                schema: "instalment_flow",
                table: "provider_inbox",
                columns: new[] { "next_attempt_at", "received_at" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ux_provider_inbox_dedupe",
                schema: "instalment_flow",
                table: "provider_inbox",
                columns: new[] { "provider", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_reconciliation_discrepancies_line",
                schema: "instalment_flow",
                table: "reconciliation_discrepancies",
                columns: new[] { "tenant_id", "provider", "provider_account", "transaction_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_provider_provider_account_window_end",
                schema: "instalment_flow",
                table: "reconciliation_runs",
                columns: new[] { "provider", "provider_account", "window_end" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_provider_accounts_tenant_id",
                schema: "instalment_flow",
                table: "tenant_provider_accounts",
                column: "tenant_id");

            // Row-level security on the tenant tables (doc 05 §4a); the SQL must never change once released.
            migrationBuilder.Sql(Schema.PaymentsSchemaSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intent_checks",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payment_intents",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "provider_inbox",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "reconciliation_discrepancies",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "reconciliation_runs",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "tenant_provider_accounts",
                schema: "instalment_flow");
        }
    }
}
