using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InstalmentFlow.EntityFrameworkCore.Migrations
{
    /// <inheritdoc />
    internal partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "instalment_flow");

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    base_currency = table.Column<string>(type: "char(3)", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => new { x.tenant_id, x.account_id });
                    table.ForeignKey(
                        name: "fk_accounts_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    command_type = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    result_type = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.tenant_id, x.key });
                    table.ForeignKey(
                        name: "fk_idempotency_keys_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journal_transactions",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_transactions", x => new { x.tenant_id, x.journal_tx_id });
                    table.ForeignKey(
                        name: "fk_journal_transactions_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "instalment_flow",
                columns: table => new
                {
                    outbox_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.outbox_id);
                    table.ForeignKey(
                        name: "fk_outbox_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pending_reversals",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    reversal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_source = table.Column<string>(type: "text", nullable: false),
                    payment_ref = table.Column<string>(type: "text", nullable: false),
                    reference_source = table.Column<string>(type: "text", nullable: true),
                    reference_value = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_reversals", x => new { x.tenant_id, x.reversal_id });
                    table.ForeignKey(
                        name: "fk_pending_reversals_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reversal_references",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    reversal_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reversal_references", x => new { x.tenant_id, x.source, x.value });
                    table.ForeignKey(
                        name: "fk_reversal_references_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_policy_versions",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    policy = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_policy_versions", x => new { x.tenant_id, x.version });
                    table.ForeignKey(
                        name: "fk_tenant_policy_versions_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unidentified_pools",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unidentified_pools", x => new { x.tenant_id, x.source });
                    table.ForeignKey(
                        name: "fk_unidentified_pools_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "obligations",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    payment_reference = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_obligations", x => new { x.tenant_id, x.obligation_id });
                    table.ForeignKey(
                        name: "fk_obligations_accounts_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "instalment_flow",
                        principalTable: "accounts",
                        principalColumns: new[] { "tenant_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unidentified_source = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    portions = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_reference_source = table.Column<string>(type: "text", nullable: true),
                    provider_reference_value = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => new { x.tenant_id, x.refund_id });
                    table.CheckConstraint("ck_refunds_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_refunds_accounts_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "instalment_flow",
                        principalTable: "accounts",
                        principalColumns: new[] { "tenant_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refunds_tenants",
                        column: x => x.tenant_id,
                        principalSchema: "instalment_flow",
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line = table.Column<short>(type: "smallint", nullable: false),
                    ledger_account = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    debit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    credit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    source_type = table.Column<short>(type: "smallint", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => new { x.tenant_id, x.journal_tx_id, x.line });
                    table.CheckConstraint("ck_ledger_entries_one_sided", "(debit > 0 and credit = 0) or (credit > 0 and debit = 0)");
                    table.ForeignKey(
                        name: "fk_ledger_entries_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_ref = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    value_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    target_kind = table.Column<short>(type: "smallint", nullable: false),
                    raw_reference = table.Column<string>(type: "text", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    identified_obligation = table.Column<Guid>(type: "uuid", nullable: true),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: true),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => new { x.tenant_id, x.payment_id });
                    table.CheckConstraint("ck_payments_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_payments_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "charges",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charges", x => new { x.tenant_id, x.charge_id });
                    table.CheckConstraint("ck_charges_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_charges_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_charges_obligations_obligation_id",
                        columns: x => new { x.tenant_id, x.obligation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "obligations",
                        principalColumns: new[] { "tenant_id", "obligation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "obligation_originated_components",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_obligation_originated_components", x => new { x.tenant_id, x.obligation_id, x.component, x.charge_code });
                    table.ForeignKey(
                        name: "fk_obligation_originated_components_obligations_obligation_id",
                        columns: x => new { x.tenant_id, x.obligation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "obligations",
                        principalColumns: new[] { "tenant_id", "obligation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: false),
                    generator = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_versions", x => new { x.tenant_id, x.obligation_id, x.version });
                    table.ForeignKey(
                        name: "fk_plan_versions_obligations_obligation_id",
                        columns: x => new { x.tenant_id, x.obligation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "obligations",
                        principalColumns: new[] { "tenant_id", "obligation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "waivers",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    waiver_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waivers", x => new { x.tenant_id, x.waiver_id, x.line });
                    table.CheckConstraint("ck_waivers_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_waivers_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_waivers_obligations_obligation_id",
                        columns: x => new { x.tenant_id, x.obligation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "obligations",
                        principalColumns: new[] { "tenant_id", "obligation_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "allocations",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    strategy_name = table.Column<string>(type: "text", nullable: false),
                    strategy_version = table.Column<int>(type: "integer", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allocations", x => new { x.tenant_id, x.allocation_id });
                    table.CheckConstraint("ck_allocations_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_allocations_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_allocations_obligations_obligation_id",
                        columns: x => new { x.tenant_id, x.obligation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "obligations",
                        principalColumns: new[] { "tenant_id", "obligation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_allocations_payments_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "payment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "funds_lots",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pool_source = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    original = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    remaining = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    reversed = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    refunded = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    deficit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    directive = table.Column<Guid>(type: "uuid", nullable: true),
                    value_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ordinal = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funds_lots", x => new { x.tenant_id, x.payment_id });
                    table.CheckConstraint("ck_funds_lots_holder", "(account_id is null) <> (pool_source is null)");
                    table.CheckConstraint("ck_funds_lots_parts", "remaining >= 0 and reversed >= 0 and refunded >= 0 and deficit >= 0");
                    table.ForeignKey(
                        name: "fk_funds_lots_accounts_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "instalment_flow",
                        principalTable: "accounts",
                        principalColumns: new[] { "tenant_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_funds_lots_payments_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "payment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_assignments",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_assignments", x => new { x.tenant_id, x.payment_id });
                    table.ForeignKey(
                        name: "fk_payment_assignments_accounts_account_id",
                        columns: x => new { x.tenant_id, x.account_id },
                        principalSchema: "instalment_flow",
                        principalTable: "accounts",
                        principalColumns: new[] { "tenant_id", "account_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_assignments_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_assignments_payments_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "payment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_exceptions",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    raw_reference = table.Column<string>(type: "text", nullable: true),
                    candidates = table.Column<string>(type: "jsonb", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_exceptions", x => new { x.tenant_id, x.exception_id });
                    table.ForeignKey(
                        name: "fk_payment_exceptions_payments_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "payment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_reversals",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    reversal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unidentified_source = table.Column<string>(type: "text", nullable: true),
                    reference_source = table.Column<string>(type: "text", nullable: true),
                    reference_value = table.Column<string>(type: "text", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    from_funds = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    unwound = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    deficit = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_reversals", x => new { x.tenant_id, x.reversal_id });
                    table.CheckConstraint("ck_payment_reversals_amount", "amount > 0 and amount = from_funds + unwound + deficit");
                    table.ForeignKey(
                        name: "fk_payment_reversals_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_reversals_payments_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "payment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "charge_reversals",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_charge_reversals", x => new { x.tenant_id, x.charge_id });
                    table.ForeignKey(
                        name: "fk_charge_reversals_charges_charge_id",
                        columns: x => new { x.tenant_id, x.charge_id },
                        principalSchema: "instalment_flow",
                        principalTable: "charges",
                        principalColumns: new[] { "tenant_id", "charge_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_charge_reversals_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instalments",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instalments", x => new { x.tenant_id, x.obligation_id, x.plan_version, x.sequence });
                    table.ForeignKey(
                        name: "fk_instalments_plan_versions_obligation_id_plan_version",
                        columns: x => new { x.tenant_id, x.obligation_id, x.plan_version },
                        principalSchema: "instalment_flow",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "obligation_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "allocation_reversals",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    cause = table.Column<short>(type: "smallint", nullable: false),
                    cause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allocation_reversals", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_allocation_reversals_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_allocation_reversals_allocations_allocation_id",
                        columns: x => new { x.tenant_id, x.allocation_id },
                        principalSchema: "instalment_flow",
                        principalTable: "allocations",
                        principalColumns: new[] { "tenant_id", "allocation_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_allocation_reversals_journal_transactions_journal_tx_id",
                        columns: x => new { x.tenant_id, x.journal_tx_id },
                        principalSchema: "instalment_flow",
                        principalTable: "journal_transactions",
                        principalColumns: new[] { "tenant_id", "journal_tx_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_exception_resolutions",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    resolved_by = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    journal_tx_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_exception_resolutions", x => new { x.tenant_id, x.exception_id });
                    table.ForeignKey(
                        name: "fk_payment_exception_resolutions_payment_exceptions_exception_id",
                        columns: x => new { x.tenant_id, x.exception_id },
                        principalSchema: "instalment_flow",
                        principalTable: "payment_exceptions",
                        principalColumns: new[] { "tenant_id", "exception_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instalment_components",
                schema: "instalment_flow",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    component = table.Column<short>(type: "smallint", nullable: false),
                    charge_code = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    due = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    allocated = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    waived = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    charged = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instalment_components", x => new { x.tenant_id, x.obligation_id, x.plan_version, x.sequence, x.component, x.charge_code });
                    table.CheckConstraint("ck_instalment_components_outstanding", "due - allocated - waived >= 0");
                    table.CheckConstraint("ck_instalment_components_parts", "due >= 0 and allocated >= 0 and waived >= 0 and charged >= 0");
                    table.ForeignKey(
                        name: "fk_instalment_components_instalments_obligation_id_plan_version_sequence",
                        columns: x => new { x.tenant_id, x.obligation_id, x.plan_version, x.sequence },
                        principalSchema: "instalment_flow",
                        principalTable: "instalments",
                        principalColumns: new[] { "tenant_id", "obligation_id", "plan_version", "sequence" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_external_id",
                schema: "instalment_flow",
                table: "accounts",
                columns: new[] { "tenant_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_allocation_reversals_tenant_id_account_id",
                schema: "instalment_flow",
                table: "allocation_reversals",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocation_reversals_tenant_id_allocation_id",
                schema: "instalment_flow",
                table: "allocation_reversals",
                columns: new[] { "tenant_id", "allocation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocation_reversals_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "allocation_reversals",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocations_tenant_id_account_id",
                schema: "instalment_flow",
                table: "allocations",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocations_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "allocations",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocations_tenant_id_obligation_id",
                schema: "instalment_flow",
                table: "allocations",
                columns: new[] { "tenant_id", "obligation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_allocations_tenant_id_payment_id",
                schema: "instalment_flow",
                table: "allocations",
                columns: new[] { "tenant_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_charge_reversals_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "charge_reversals",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_charges_tenant_id_account_id",
                schema: "instalment_flow",
                table: "charges",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_charges_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "charges",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_charges_tenant_id_obligation_id",
                schema: "instalment_flow",
                table: "charges",
                columns: new[] { "tenant_id", "obligation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_funds_lots_tenant_id_account_id",
                schema: "instalment_flow",
                table: "funds_lots",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_funds_lots_tenant_id_pool_source",
                schema: "instalment_flow",
                table: "funds_lots",
                columns: new[] { "tenant_id", "pool_source" });

            migrationBuilder.CreateIndex(
                name: "ix_journal_transactions_tenant_id_account_id_seq",
                schema: "instalment_flow",
                table: "journal_transactions",
                columns: new[] { "tenant_id", "account_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ux_journal_seq",
                schema: "instalment_flow",
                table: "journal_transactions",
                column: "seq",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_tenant_id_ledger_account",
                schema: "instalment_flow",
                table: "ledger_entries",
                columns: new[] { "tenant_id", "ledger_account" });

            migrationBuilder.CreateIndex(
                name: "ix_obligations_tenant_id_account_id",
                schema: "instalment_flow",
                table: "obligations",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ux_obligations_external_id",
                schema: "instalment_flow",
                table: "obligations",
                columns: new[] { "tenant_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_obligations_payment_reference",
                schema: "instalment_flow",
                table: "obligations",
                columns: new[] { "tenant_id", "payment_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_id",
                schema: "instalment_flow",
                table: "outbox",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "instalment_flow",
                table: "outbox",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_tenant_id_account_id_outbox_id",
                schema: "instalment_flow",
                table: "outbox",
                columns: new[] { "tenant_id", "account_id", "outbox_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_assignments_tenant_id_account_id",
                schema: "instalment_flow",
                table: "payment_assignments",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_assignments_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "payment_assignments",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_exceptions_tenant_id_opened_at",
                schema: "instalment_flow",
                table: "payment_exceptions",
                columns: new[] { "tenant_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_exceptions_tenant_id_payment_id",
                schema: "instalment_flow",
                table: "payment_exceptions",
                columns: new[] { "tenant_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_reversals_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "payment_reversals",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_reversals_tenant_id_payment_id",
                schema: "instalment_flow",
                table: "payment_reversals",
                columns: new[] { "tenant_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "payments",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ux_payments_source",
                schema: "instalment_flow",
                table: "payments",
                columns: new[] { "tenant_id", "source", "external_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pending_reversals_tenant_id_payment_source_payment_ref",
                schema: "instalment_flow",
                table: "pending_reversals",
                columns: new[] { "tenant_id", "payment_source", "payment_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_refunds_tenant_id_account_id",
                schema: "instalment_flow",
                table: "refunds",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_waivers_tenant_id_account_id",
                schema: "instalment_flow",
                table: "waivers",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_waivers_tenant_id_journal_tx_id",
                schema: "instalment_flow",
                table: "waivers",
                columns: new[] { "tenant_id", "journal_tx_id" });

            migrationBuilder.CreateIndex(
                name: "ix_waivers_tenant_id_obligation_id",
                schema: "instalment_flow",
                table: "waivers",
                columns: new[] { "tenant_id", "obligation_id" });

            // Triggers, the balance check and row-level security (doc 05 §4, §4a).
            migrationBuilder.Sql(Schema.SchemaSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocation_reversals",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "charge_reversals",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "funds_lots",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "idempotency_keys",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "instalment_components",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "ledger_entries",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "obligation_originated_components",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payment_assignments",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payment_exception_resolutions",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payment_reversals",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "pending_reversals",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "reversal_references",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "tenant_policy_versions",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "unidentified_pools",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "waivers",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "allocations",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "charges",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "instalments",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payment_exceptions",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "plan_versions",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "payments",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "obligations",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "journal_transactions",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "accounts",
                schema: "instalment_flow");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "instalment_flow");

            migrationBuilder.Sql(Schema.SchemaSql.Down);
        }
    }
}
