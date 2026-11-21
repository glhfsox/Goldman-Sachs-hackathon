using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.id);
                    table.CheckConstraint("CK_sources_kind", "kind IN ('http', 'kafka')");
                });

            // The four sources of contracts/data/sources.md section 1. Their records are queued in the same
            // migration, so ingest receives them on config.changes like any later change.
            migrationBuilder.Sql(@"
                INSERT INTO sources (id, kind, config, enabled, updated_at) VALUES
                    ('workflows-kafka', 'kafka', '{""topic"": ""source.workflows""}', true, now()),
                    ('trades-kafka', 'kafka', '{""topic"": ""source.trades""}', true, now()),
                    ('alertmanager-http', 'http', '{}', true, now()),
                    ('pricing-logs-http', 'http', '{}', true, now());

                INSERT INTO outbox (topic, key, payload, created_at)
                SELECT 'config.changes', 'source:' || id,
                    jsonb_build_object(
                        'kind', 'source',
                        'id', id,
                        'updated_at', to_char(updated_at AT TIME ZONE 'UTC', 'YYYY-MM-DD""T""HH24:MI:SS.MS""Z""'),
                        'deleted', false,
                        'body', jsonb_build_object('id', id, 'kind', kind, 'config', config, 'enabled', enabled)),
                    now()
                FROM sources ORDER BY id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sources");
        }
    }
}
