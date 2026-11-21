using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    rule_id = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<string>(type: "text", nullable: false),
                    incident_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    fields = table.Column<string>(type: "jsonb", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    runbook_id = table.Column<string>(type: "text", nullable: true),
                    acked_by = table.Column<string>(type: "text", nullable: true),
                    resolved_by = table.Column<string>(type: "text", nullable: true),
                    sample_event = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    alert_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    send_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "schema_samples",
                columns: table => new
                {
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    sample = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_samples", x => x.fingerprint);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_alert_id_at",
                table: "alert_events",
                columns: new[] { "alert_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_first_seen",
                table: "alerts",
                column: "first_seen");

            migrationBuilder.CreateIndex(
                name: "IX_alerts_team_id_first_seen",
                table: "alerts",
                columns: new[] { "team_id", "first_seen" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_alert_id",
                table: "notifications",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "IX_schema_samples_status",
                table: "schema_samples",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "schema_samples");
        }
    }
}
