using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_deps",
                columns: table => new
                {
                    service = table.Column<string>(type: "text", nullable: false),
                    depends_on = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_deps", x => new { x.service, x.depends_on });
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    team_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    on_call = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.team_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_memberships_team",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memberships_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "preferences",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_preferences_user",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memberships_user_id",
                table: "memberships",
                column: "user_id");

            // The brief's teams and people (data/README.md), so the UI pickers and the notifier have someone to
            // address from the first start. Each row is also queued as a config.changes record.
            migrationBuilder.Sql(@"
                INSERT INTO teams (id, name) VALUES
                    ('settlements', 'Settlements'), ('pricing', 'Pricing'), ('rates', 'Rates'), ('payments', 'Payments');
                INSERT INTO users (id, name, email, phone) VALUES
                    ('anna', 'Anna Kowalska', 'anna@example.com', '+48500000001'),
                    ('boris', 'Boris Nowak', 'boris@example.com', '+48500000002'),
                    ('clara', 'Clara Wisniewska', 'clara@example.com', '+48500000003'),
                    ('dmytro', 'Dmytro Melnyk', 'dmytro@example.com', '+48500000004'),
                    ('elena', 'Elena Zielinska', 'elena@example.com', '+48500000005');
                INSERT INTO memberships (team_id, user_id, on_call) VALUES
                    ('settlements', 'boris', true), ('settlements', 'elena', false),
                    ('pricing', 'clara', true), ('rates', 'dmytro', true), ('payments', 'anna', true);
                INSERT INTO service_deps (service, depends_on) VALUES
                    ('payments-api', 'counterparty-service'), ('order-settlement', 'counterparty-service');
                INSERT INTO preferences (user_id, body) VALUES
                    ('anna', '{""user_id"": ""anna"", ""timezone"": ""Europe/Warsaw"",
                        ""by_severity"": {""critical"": [""sms""], ""error"": [""email""], ""warning"": [""email""], ""info"": [""ui""]},
                        ""quiet_hours"": {""start"": ""20:00"", ""end"": ""08:00"", ""downgrade"": {""call"": ""sms"", ""sms"": ""email""}},
                        ""wake_me"": false, ""max_push_per_hour"": 10}'),
                    ('boris', '{""user_id"": ""boris"", ""timezone"": ""Europe/Warsaw"",
                        ""by_severity"": {""critical"": [""call"", ""sms""], ""error"": [""sms""], ""warning"": [""email""], ""info"": [""ui""]},
                        ""wake_me"": true, ""max_push_per_hour"": 20}');

                INSERT INTO outbox (topic, key, payload, created_at)
                SELECT 'config.changes', record.kind || ':' || record.id,
                    jsonb_build_object('kind', record.kind, 'id', record.id,
                        'updated_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD""T""HH24:MI:SS.MS""Z""'),
                        'deleted', false, 'body', record.body),
                    now()
                FROM (
                    SELECT 'team' AS kind, id, jsonb_build_object('id', id, 'name', name) AS body FROM teams
                    UNION ALL
                    SELECT 'user', id, jsonb_build_object('id', id, 'name', name, 'email', email, 'phone', phone) FROM users
                    UNION ALL
                    SELECT 'membership', team_id || ':' || user_id,
                        jsonb_build_object('team_id', team_id, 'user_id', user_id, 'on_call', on_call) FROM memberships
                    UNION ALL
                    SELECT 'service_dep', service || ':' || depends_on,
                        jsonb_build_object('service', service, 'depends_on', depends_on) FROM service_deps
                    UNION ALL
                    SELECT 'preferences', user_id, body FROM preferences
                ) AS record
                ORDER BY record.kind, record.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropTable(
                name: "preferences");

            migrationBuilder.DropTable(
                name: "service_deps");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
