using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActionTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "action_targets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    origins = table.Column<string>(type: "jsonb", nullable: false),
                    operations = table.Column<string>(type: "jsonb", nullable: false),
                    secret_ref = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_action_targets", x => x.id);
                });

            // Demo targets: every action lands on mock-target under the target's id (contracts/runbooks.md §3).
            // Real systems are a PUT /api/action-targets/{id} away; nothing here is code.
            migrationBuilder.Sql("""
                INSERT INTO action_targets (id, base_url, origins, operations, version, updated_at) VALUES
                ('workflow-engine', 'http://mock-target:8003/workflow-engine', '[]'::jsonb, '{"retry_workflow": {"method": "POST", "path": "/workflows/{workflowId}/retry"}}'::jsonb, 1, now()),
                ('services', 'http://mock-target:8003/services', '[]'::jsonb, '{"restart_service": {"method": "POST", "path": "/restart"}, "health": {"method": "GET", "path": "/health", "risk": "read_only"}}'::jsonb, 1, now()),
                ('orders-db', 'http://mock-target:8003/orders-db', '[]'::jsonb, '{"health": {"method": "GET", "path": "/instances/{instance}/health", "risk": "read_only"}, "reload_db": {"method": "POST", "path": "/instances/{instance}/reload"}, "restart_service": {"method": "POST", "path": "/instances/{instance}/restart"}}'::jsonb, 1, now()),
                ('webhooks', 'http://mock-target:8003/webhooks', '[]'::jsonb, '{"webhook": {"method": "POST", "path": "/hook"}}'::jsonb, 1, now()),
                ('ticketing', 'http://mock-target:8003/ticketing', '[]'::jsonb, '{"ticket": {"method": "POST", "path": "/tickets"}}'::jsonb, 1, now()),
                ('escalation', 'http://mock-target:8003/escalation', '[]'::jsonb, '{"escalate": {"method": "POST", "path": "/escalations"}}'::jsonb, 1, now()),
                ('counterparty-service', 'http://mock-target:8003/counterparty-service', '["http://counterparty-service"]'::jsonb, '{"http_get": {"method": "GET", "path": "/health"}}'::jsonb, 1, now()),
                ('payments-api', 'http://mock-target:8003/payments-api', '["http://payments-api"]'::jsonb, '{"http_get": {"method": "GET", "path": "/health"}, "restart_service": {"method": "POST", "path": "/restart"}}'::jsonb, 1, now()),
                ('pricing-engine', 'http://mock-target:8003/pricing-engine', '["http://pricing-engine"]'::jsonb, '{"http_get": {"method": "GET", "path": "/health"}, "restart_service": {"method": "POST", "path": "/instances/{instance}/restart"}}'::jsonb, 1, now());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "action_targets");
        }
    }
}
