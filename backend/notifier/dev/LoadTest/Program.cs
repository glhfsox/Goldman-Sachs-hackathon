// Load test of the notifier stage alone: alerts.changes -> notification planned -> sent on its channel.
// It stands in for the api (config.changes) and the processor (alerts.changes), then reads the notifier's
// own notifications table for the timings.
//   dotnet run --project backend/notifier/dev/LoadTest -- --label baseline-burst --alerts 2000 --rate 0
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using Npgsql;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var options = Args.Parse(args);
var run = options.RunId;
Console.WriteLine($"run {run}: {options.Alerts} alerts, rate {(options.Rate == 0 ? "burst" : options.Rate + "/s")}, " +
    $"{options.Users} users x [{string.Join(",", options.Channels)}]");

await using var db = NpgsqlDataSource.Create(options.Db);
var skew = await ClockSkewAsync(db);
Console.WriteLine($"database clock - host clock: {skew.TotalMilliseconds:0.0} ms (corrected below)");

using var producer = new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = options.Brokers,
    Acks = Acks.All,
    EnableIdempotence = true,
    LingerMs = 5
}).Build();

// 1. Configuration the api would publish.
var team = $"lt-{run}";
var users = Enumerable.Range(1, options.Users).Select(index => $"lt-{run}-u{index}").ToList();
var rule = $"lt-{run}-rule";
var configAt = DateTime.UtcNow.ToString("O");
var configRecords = new List<(string Key, object Record)>
{
    ($"team:{team}", Config("team", team, configAt, new { id = team, name = $"Load test {run}" }))
};
foreach (var user in users)
{
    configRecords.Add(($"user:{user}", Config("user", user, configAt, new { id = user, name = user, email = $"{user}@example.com", phone = "+48000000999" })));
    configRecords.Add(($"membership:{team}:{user}", Config("membership", $"{team}:{user}", configAt, new { team_id = team, user_id = user, on_call = false })));
}

configRecords.Add(($"rule:{rule}", Config("rule", rule, configAt, new
{
    id = rule,
    team_id = team,
    enabled = true,
    window_s = 300,
    route = new { recipients = new[] { new { team } }, channels = options.Channels }
})));
foreach (var (key, record) in configRecords)
{
    producer.Produce("config.changes", new Message<string, string> { Key = key, Value = Json(record) });
}

producer.Flush(TimeSpan.FromSeconds(10));
await WaitAsync("config copies", TimeSpan.FromSeconds(60), async () =>
    await ScalarAsync<long>(db, "select count(*) from memberships where team_id = @p", team) == users.Count
    && await ScalarAsync<long>(db, "select count(*) from rules where id = @p", rule) == 1);

// 2. Alert changes the processor would publish: unique alert, change and event ids, so nothing is deduplicated.
var expected = options.Alerts * users.Count * options.Channels.Length;
var producedAt = new DateTime[options.Alerts];
var clock = Stopwatch.StartNew();
var started = DateTime.UtcNow;
for (var index = 0; index < options.Alerts; index++)
{
    if (options.Rate > 0)
    {
        var due = TimeSpan.FromSeconds(index / (double)options.Rate);
        if (due > clock.Elapsed)
        {
            await Task.Delay(due - clock.Elapsed);
        }
    }

    var now = DateTime.UtcNow;
    producedAt[index] = now;
    var alertId = $"lt-{run}-a{index}";
    var record = new
    {
        change_id = $"lt-{run}-c{index}",
        change = "opened",
        at = now.ToString("O"),
        actor = "processor",
        alert = new
        {
            id = alertId,
            rule_id = rule,
            team_id = team,
            incident_id = (string?)null,
            status = "open",
            severity = "critical",
            title = $"Load test alert {index}",
            body = "Synthetic alert from the notifier load test",
            fields = new Dictionary<string, object> { ["index"] = index },
            count = 1,
            first_received_at = now.ToString("O"),
            sample_event = new { event_id = $"lt-{run}-e{index}", raw = new { index } }
        }
    };
    producer.Produce("alerts.changes", new Message<string, string> { Key = alertId, Value = Json(record) });
}

producer.Flush(TimeSpan.FromSeconds(30));
var produceSeconds = (DateTime.UtcNow - started).TotalSeconds;
Console.WriteLine($"produced {options.Alerts} alert changes in {produceSeconds:0.00} s; expecting {expected} notifications");

// 3. Wait until every notification of the run is final.
var pattern = $"lt-{run}-a%";
await WaitAsync("notifications to finish", TimeSpan.FromSeconds(options.TimeoutSeconds), async () =>
    await ScalarAsync<long>(db, "select count(*) from notifications where alert_id like @p and status in ('sent','dead','cancelled','suppressed')", pattern) >= expected);

// 4. Timings.
var rows = new List<Row>();
await using (var command = db.CreateCommand("select alert_id, channel, status, created_at, sent_at from notifications where alert_id like @p"))
{
    command.Parameters.AddWithValue("p", pattern);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var alertIndex = int.Parse(reader.GetString(0)[(pattern.Length - 1)..], CultureInfo.InvariantCulture);
        rows.Add(new Row(
            reader.GetString(1),
            reader.GetString(2),
            producedAt[alertIndex],
            reader.GetDateTime(3).ToUniversalTime() - skew,
            reader.IsDBNull(4) ? null : reader.GetDateTime(4).ToUniversalTime() - skew));
    }
}

var report = Report.Build(options, run, rows, expected, produceSeconds, skew);
Console.WriteLine(report);
var output = Path.Combine(options.OutputDir, $"results-{options.Label}.md");
Directory.CreateDirectory(options.OutputDir);
await File.WriteAllTextAsync(output, report);
Console.WriteLine($"written to {output}");

static object Config(string kind, string id, string updatedAt, object body) =>
    new { kind, id, updated_at = updatedAt, deleted = false, body };

static string Json(object value) => JsonSerializer.Serialize(value);

static async Task<T> ScalarAsync<T>(NpgsqlDataSource db, string sql, string parameter)
{
    await using var command = db.CreateCommand(sql);
    command.Parameters.AddWithValue("p", parameter);

    return (T)(await command.ExecuteScalarAsync())!;
}

static async Task WaitAsync(string what, TimeSpan timeout, Func<Task<bool>> done)
{
    var clock = Stopwatch.StartNew();
    while (!await done())
    {
        if (clock.Elapsed > timeout)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
        }

        await Task.Delay(100);
    }
}

// Containers run on the Docker VM clock; the offset is measured so timings compare like with like.
static async Task<TimeSpan> ClockSkewAsync(NpgsqlDataSource db)
{
    var samples = new List<double>();
    for (var attempt = 0; attempt < 5; attempt++)
    {
        var before = DateTime.UtcNow;
        await using var command = db.CreateCommand("select clock_timestamp()");
        var dbNow = ((DateTime)(await command.ExecuteScalarAsync())!).ToUniversalTime();
        var after = DateTime.UtcNow;
        samples.Add((dbNow - (before + (after - before) / 2)).TotalMilliseconds);
    }

    samples.Sort();

    return TimeSpan.FromMilliseconds(samples[samples.Count / 2]);
}

internal sealed record Row(string Channel, string Status, DateTime ProducedAt, DateTime PlannedAt, DateTime? SentAt);

internal sealed record Args(
    string Label,
    string RunId,
    int Alerts,
    int Rate,
    int Users,
    string[] Channels,
    string Brokers,
    string Db,
    int TimeoutSeconds,
    string OutputDir)
{
    public static Args Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            values[args[index].TrimStart('-')] = args[index + 1];
        }

        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);

        return new Args(
            Get("label", "run"),
            Get("run-id", DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture)),
            int.Parse(Get("alerts", "1000"), CultureInfo.InvariantCulture),
            int.Parse(Get("rate", "0"), CultureInfo.InvariantCulture),
            int.Parse(Get("users", "3"), CultureInfo.InvariantCulture),
            Get("channels", "ui,email,sms").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Get("brokers", "localhost:9092"),
            Get("db", "Host=localhost;Port=5435;Database=notifier;Username=alerts;Password=alerts"),
            int.Parse(Get("timeout", "600"), CultureInfo.InvariantCulture),
            Get("out", "dev/loadtest"));
    }
}

internal static class Report
{
    public static string Build(Args options, string run, List<Row> rows, int expected, double produceSeconds, TimeSpan skew)
    {
        var sent = rows.Where(row => row.SentAt is not null).ToList();
        var builder = new StringBuilder();
        builder.AppendLine($"# Notifier load test: {options.Label}");
        builder.AppendLine();
        builder.AppendLine($"- Run `{run}`, {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. Notifier stage only: `alerts.changes` produced -> notification planned -> sent by the channel adapter.");
        builder.AppendLine($"- Load: {options.Alerts} critical `opened` changes, {(options.Rate == 0 ? "as fast as possible" : $"{options.Rate}/s")} " +
            $"(produced in {produceSeconds:0.00} s), {options.Users} recipients x [{string.Join(", ", options.Channels)}] = {expected} notifications.");
        builder.AppendLine($"- Database/host clock offset {skew.TotalMilliseconds:0.0} ms, corrected.");
        builder.AppendLine();

        var statuses = rows.GroupBy(row => row.Status).Select(group => $"{group.Key} {group.Count()}");
        builder.AppendLine($"Rows: {rows.Count} of {expected} ({string.Join(", ", statuses)}).");
        if (sent.Count > 0)
        {
            var first = rows.Min(row => row.ProducedAt);
            var last = sent.Max(row => row.SentAt!.Value);
            var seconds = Math.Max(0.001, (last - first).TotalSeconds);
            builder.AppendLine($"Throughput: {sent.Count} sent in {seconds:0.00} s = **{sent.Count / seconds:0} notifications/s** " +
                $"({sent.Count / seconds / (options.Users * options.Channels.Length):0} alerts/s fully delivered).");
        }

        builder.AppendLine();
        builder.AppendLine("| Channel | Stage | p50 ms | p95 ms | p99 ms | max ms |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in sent.GroupBy(row => row.Channel).OrderBy(group => group.Key).Append(sent.GroupBy(_ => "all").Single()))
        {
            Line(builder, group.Key, "produced -> planned", group.Select(row => (row.PlannedAt - row.ProducedAt).TotalMilliseconds));
            Line(builder, group.Key, "planned -> sent", group.Select(row => (row.SentAt!.Value - row.PlannedAt).TotalMilliseconds));
            Line(builder, group.Key, "**produced -> sent**", group.Select(row => (row.SentAt!.Value - row.ProducedAt).TotalMilliseconds));
        }

        return builder.ToString();
    }

    private static void Line(StringBuilder builder, string channel, string stage, IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        builder.AppendLine($"| {channel} | {stage} | {P(sorted, 50):0} | {P(sorted, 95):0} | {P(sorted, 99):0} | {sorted[^1]:0} |");
    }

    private static double P(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
