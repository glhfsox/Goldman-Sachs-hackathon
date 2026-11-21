using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlertApi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSeedRulesAndMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The brief's mappings and example rules (contracts/rules.md §3 and §4), so a fresh start already turns
            // the sample events into alerts. Each row is also queued as a config.changes record.
            migrationBuilder.Sql(@"
                INSERT INTO mappings (id, status, origin, priority, body, updated_at)
                SELECT id, status, origin, priority, body, now() FROM (VALUES
                    ('workflow-failed', 'active', 'seed', 100, $json${""id"":""workflow-failed"",""status"":""active"",""origin"":""seed"",""priority"":100,""match"":{""all"":[{""field"":""raw.type"",""op"":""eq"",""value"":""WORKFLOW_FAILED""}]},""fields"":{""type"":{""path"":""type""},""service"":{""path"":""workflowName""},""severity"":{""path"":""severity""},""env"":{""path"":""environment""},""ts"":{""path"":""timestamp"",""format"":""iso""}},""dedup_fields"":[""workflowName"",""error.code""]}$json$::jsonb),
                    ('alertmanager', 'active', 'seed', 100, $json${""id"":""alertmanager"",""status"":""active"",""origin"":""seed"",""priority"":100,""match"":{""all"":[{""field"":""source"",""op"":""eq"",""value"":""alertmanager-http""},{""field"":""raw.alertname"",""op"":""exists""}]},""fields"":{""type"":{""path"":""alertname""},""service"":{""path"":""labels.service""},""severity"":{""path"":""labels.severity""},""env"":{""const"":""prod""},""ts"":{""path"":""startsAt"",""format"":""iso""}},""dedup_fields"":[""labels.region""]}$json$::jsonb),
                    ('trade-rejected-v1', 'active', 'llm', 100, $json${""id"":""trade-rejected-v1"",""status"":""active"",""origin"":""llm"",""priority"":100,""match"":{""all"":[{""field"":""raw.meta.schema"",""op"":""glob"",""value"":""trade.rejected*""}]},""fields"":{""type"":{""path"":""meta.schema""},""service"":{""path"":""meta.producer""},""severity"":{""const"":""error""},""env"":{""const"":""prod""},""ts"":{""path"":""meta.ts"",""format"":""epoch_ms""}},""severity_map"":{""P1"":""critical"",""ERROR"":""error"",""WARN"":""warning""},""dedup_fields"":[""payload.desk"",""payload.reason""]}$json$::jsonb),
                    ('pricing-logs', 'active', 'seed', 50, $json${""id"":""pricing-logs"",""status"":""active"",""origin"":""seed"",""priority"":50,""match"":{""all"":[{""field"":""raw.logger"",""op"":""glob"",""value"":""pricing-engine*""}]},""fields"":{""type"":{""const"":""log""},""service"":{""path"":""logger""},""severity"":{""path"":""level""},""env"":{""const"":""prod""},""ts"":{""path"":""time"",""format"":""epoch_s""}},""severity_map"":{""ERR"":""error"",""FATAL"":""critical""}}$json$::jsonb)
                ) AS seed (id, status, origin, priority, body);

                INSERT INTO rules (id, team_id, enabled, body, updated_at)
                SELECT id, team_id, enabled, body, now() FROM (VALUES
                    ('settlements-workflow-failed', 'settlements', true, $json${""id"":""settlements-workflow-failed"",""team_id"":""settlements"",""name"":""Order settlement failed in prod"",""enabled"":true,""when"":{""all"":[{""field"":""type"",""op"":""eq"",""value"":""WORKFLOW_FAILED""},{""field"":""raw.workflowName"",""op"":""eq"",""value"":""order-settlement""},{""field"":""env"",""op"":""eq"",""value"":""prod""}]},""alert_key"":[""raw.workflowName"",""raw.error.code""],""window_s"":300,""route"":{""recipients"":[{""team"":""settlements"",""on_call"":true}],""channels"":[""call"",""sms"",""ui""]},""present"":{""title"":""{{workflowName}} failed at step {{step}}"",""body"":""{{error.message}}"",""fields"":[""step"",""error.code"",""error.message""],""color"":""red""},""runbook_id"":""settlement-timeout""}$json$::jsonb),
                    ('pricing-engine-errors', 'pricing', true, $json${""id"":""pricing-engine-errors"",""team_id"":""pricing"",""when"":{""all"":[{""field"":""service"",""op"":""glob"",""value"":""pricing-engine*""},{""field"":""severity"",""op"":""sev_gte"",""value"":""error""}]},""alert_key"":[""raw.instrument""],""window_s"":300,""route"":{""recipients"":[{""team"":""pricing""}],""channels"":[""ui""]},""present"":{""title"":""Pricing errors on {{instrument}}"",""color"":""orange""}}$json$::jsonb),
                    ('rates-large-rejections', 'rates', true, $json${""id"":""rates-large-rejections"",""team_id"":""rates"",""when"":{""all"":[{""field"":""type"",""op"":""glob"",""value"":""trade.rejected*""},{""field"":""payload.desk"",""op"":""eq"",""value"":""rates""},{""field"":""payload.notional"",""op"":""gt"",""value"":10000000}]},""severity"":""critical"",""route"":{""recipients"":[{""team"":""rates""}],""channels"":[""email"",""ui""]},""present"":{""title"":""Trade {{payload.tradeId}} rejected: {{payload.reason}} ({{payload.notional}} {{payload.currency}})""}}$json$::jsonb),
                    ('anna-payments-critical', 'payments', true, $json${""id"":""anna-payments-critical"",""team_id"":""payments"",""when"":{""all"":[{""field"":""service"",""op"":""eq"",""value"":""payments-api""},{""field"":""severity"",""op"":""eq"",""value"":""critical""}]},""route"":{""recipients"":[{""user"":""anna""}]}}$json$::jsonb)
                ) AS seed (id, team_id, enabled, body);

                INSERT INTO outbox (topic, key, payload, created_at)
                SELECT 'config.changes', record.kind || ':' || record.id,
                    jsonb_build_object('kind', record.kind, 'id', record.id,
                        'updated_at', to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD""T""HH24:MI:SS.MS""Z""'),
                        'deleted', false, 'body', record.body),
                    now()
                FROM (
                    SELECT 'mapping' AS kind, id, body FROM mappings
                    UNION ALL
                    SELECT 'rule', id, body FROM rules
                ) AS record
                ORDER BY record.kind, record.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM rules WHERE id IN ('settlements-workflow-failed', 'pricing-engine-errors', 'rates-large-rejections', 'anna-payments-critical');
                DELETE FROM mappings WHERE id IN ('workflow-failed', 'alertmanager', 'trade-rejected-v1', 'pricing-logs');");
        }
    }
}
