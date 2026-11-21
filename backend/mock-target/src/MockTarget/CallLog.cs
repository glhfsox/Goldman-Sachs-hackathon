namespace MockTarget;

public sealed record Call(
    DateTimeOffset At,
    string Method,
    string Path,
    string Query,
    string Body,
    string? IdempotencyKey,
    int Status,
    bool Replay);

public sealed record FailRule(
    string PathPrefix,
    int Times,
    int Status);

public sealed record StoredResponse(
    int Status,
    string Body);

// Everything runbook actions did, in memory: the demo and the tests read it back through GET /_calls.
public sealed class CallLog
{
    private readonly Lock _lock = new();
    private readonly List<Call> _calls = [];
    private readonly Dictionary<string, StoredResponse> _byKey = [];
    private readonly List<FailRule> _failures = [];

    public IReadOnlyList<Call> All()
    {
        lock (_lock)
        {
            return [.. _calls];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _calls.Clear();
            _byKey.Clear();
            _failures.Clear();
        }
    }

    public void AddFailure(FailRule rule)
    {
        lock (_lock)
        {
            _failures.Add(rule);
        }
    }

    // A repeated Idempotency-Key gets the first answer again and does nothing new, like a real idempotent API.
    public StoredResponse Handle(DateTimeOffset at, string method, string path, string query, string body, string? key)
    {
        lock (_lock)
        {
            if (key is not null && _byKey.TryGetValue(key, out var stored))
            {
                _calls.Add(new Call(at, method, path, query, body, key, stored.Status, Replay: true));

                return stored;
            }

            var response = NextFailure(path) is { } status
                ? new StoredResponse(status, "{\"ok\":false,\"error\":\"mock failure\"}")
                : new StoredResponse(200, $"{{\"ok\":true,\"path\":\"{path}\"}}");

            if (key is not null)
            {
                _byKey[key] = response;
            }

            _calls.Add(new Call(at, method, path, query, body, key, response.Status, Replay: false));

            return response;
        }
    }

    private int? NextFailure(string path)
    {
        var index = _failures.FindIndex(rule => path.StartsWith(rule.PathPrefix, StringComparison.Ordinal));

        if (index < 0)
        {
            return null;
        }

        var rule = _failures[index];
        _failures[index] = rule with { Times = rule.Times - 1 };

        if (rule.Times <= 1)
        {
            _failures.RemoveAt(index);
        }

        return rule.Status;
    }
}
