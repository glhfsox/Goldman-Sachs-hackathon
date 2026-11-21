using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRunbookRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "action_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: false),
                    runbook_id = table.Column<string>(type: "text", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true),
                    @params = table.Column<string>(name: "params", type: "jsonb", nullable: true),
                    risk = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    target_version = table.Column<int>(type: "integer", nullable: true),
                    request = table.Column<string>(type: "jsonb", nullable: true),
                    needs = table.Column<string>(type: "jsonb", nullable: false),
                    if_step = table.Column<string>(type: "text", nullable: true),
                    if_status = table.Column<string>(type: "text", nullable: true),
                    retries = table.Column<int>(type: "integer", nullable: false),
                    timeout_s = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runbook_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: false),
                    runbook_id = table.Column<string>(type: "text", nullable: false),
                    runbook_version = table.Column<int>(type: "integer", nullable: false),
                    risk = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runbook_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_action_runs_alert_id",
                table: "action_runs",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "IX_action_runs_claimable",
                table: "action_runs",
                column: "not_before",
                filter: "status in ('ready', 'running')");

            migrationBuilder.CreateIndex(
                name: "UQ_action_runs_idempotency_key",
                table: "action_runs",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_action_runs_run_id_node_id",
                table: "action_runs",
                columns: new[] { "run_id", "node_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runbook_runs_alert_id_created_at",
                table: "runbook_runs",
                columns: new[] { "alert_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_runbook_runs_status_finished_at",
                table: "runbook_runs",
                columns: new[] { "status", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "UQ_runbook_runs_live_alert",
                table: "runbook_runs",
                column: "alert_id",
                unique: true,
                filter: "status in ('scheduled', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_runs");

            migrationBuilder.DropTable(
                name: "runbook_runs");
        }
    }
}
