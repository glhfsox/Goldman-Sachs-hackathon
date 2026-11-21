namespace AlertApi.Domain.Models;

// Where runbook actions go (spec §5a of #73): a system, its base URL, the origins a runbook may name it by and the
// operations it offers. Only the api calls these endpoints, so the table is not published on config.changes.
public class ActionTarget
{
    private ActionTarget()
    {
    }

    public string Id { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

    // JSON array of origins such as "http://counterparty-service".
    public string Origins { get; private set; } = null!;

    // JSON object: operation name -> {method, path, risk?}.
    public string Operations { get; private set; } = null!;

    public string? SecretRef { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ActionTarget Create(string id, string baseUrl, string origins, string operations, string? secretRef, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var target = new ActionTarget { Id = id };
        target.Update(baseUrl, origins, operations, secretRef, updatedAt);

        return target;
    }

    // Every change is a new version: a run records the version its requests were built from.
    public void Update(string baseUrl, string origins, string operations, string? secretRef, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(origins);
        ArgumentException.ThrowIfNullOrWhiteSpace(operations);

        BaseUrl = baseUrl;
        Origins = origins;
        Operations = operations;
        SecretRef = secretRef;
        Version++;
        UpdatedAt = updatedAt;
    }
}
