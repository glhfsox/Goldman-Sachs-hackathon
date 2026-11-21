using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRunRiskCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UQ_runbook_runs_live_alert",
                table: "runbook_runs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "risk_check_at",
                table: "runbook_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "risk_check_by",
                table: "runbook_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "risk_check_confidence",
                table: "runbook_runs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "risk_check_reasons",
                table: "runbook_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "risk_check_verdict",
                table: "runbook_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_runbook_runs_risk_check_pending",
                table: "runbook_runs",
                column: "status",
                filter: "status = 'scheduled' and risk_check_verdict is null");

            migrationBuilder.CreateIndex(
                name: "UQ_runbook_runs_live_alert",
                table: "runbook_runs",
                column: "alert_id",
                unique: true,
                filter: "status in ('scheduled', 'waiting_approval', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_runbook_runs_risk_check_pending",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "UQ_runbook_runs_live_alert",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "risk_check_at",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "risk_check_by",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "risk_check_confidence",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "risk_check_reasons",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "risk_check_verdict",
                table: "runbook_runs");

            migrationBuilder.CreateIndex(
                name: "UQ_runbook_runs_live_alert",
                table: "runbook_runs",
                column: "alert_id",
                unique: true,
                filter: "status in ('scheduled', 'running')");
        }
    }
}
