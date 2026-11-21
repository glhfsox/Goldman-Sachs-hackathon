using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ingest.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
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

            // Seed rows from contracts/data/sources.md. updated_at is old on purpose:
            // any record from config.changes replaces them.
            migrationBuilder.Sql(@"
                INSERT INTO sources (id, kind, config, enabled, updated_at) VALUES
                    ('workflows-kafka', 'kafka', '{""topic"": ""source.workflows""}', true, '2000-01-01T00:00:00Z'),
                    ('trades-kafka', 'kafka', '{""topic"": ""source.trades""}', true, '2000-01-01T00:00:00Z'),
                    ('alertmanager-http', 'http', '{}', true, '2000-01-01T00:00:00Z'),
                    ('pricing-logs-http', 'http', '{}', true, '2000-01-01T00:00:00Z');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sources");
        }
    }
}
