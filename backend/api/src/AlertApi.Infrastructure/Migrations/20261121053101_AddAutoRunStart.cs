using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoRunStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trigger_change_id",
                table: "runbook_runs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "runbook_starts",
                columns: table => new
                {
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    runbook_id = table.Column<string>(type: "text", nullable: false),
                    start = table.Column<string>(type: "text", nullable: false),
                    set_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runbook_starts", x => new { x.rule_id, x.runbook_id });
                    table.CheckConstraint("CK_runbook_starts_start", "start in ('auto', 'approval')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_runbook_runs_scheduled_start_at",
                table: "runbook_runs",
                column: "start_at",
                filter: "status = 'scheduled'");

            migrationBuilder.CreateIndex(
                name: "UQ_runbook_runs_trigger_change_id",
                table: "runbook_runs",
                column: "trigger_change_id",
                unique: true,
                filter: "trigger_change_id is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runbook_starts");

            migrationBuilder.DropIndex(
                name: "IX_runbook_runs_scheduled_start_at",
                table: "runbook_runs");

            migrationBuilder.DropIndex(
                name: "UQ_runbook_runs_trigger_change_id",
                table: "runbook_runs");

            migrationBuilder.DropColumn(
                name: "trigger_change_id",
                table: "runbook_runs");
        }
    }
}
