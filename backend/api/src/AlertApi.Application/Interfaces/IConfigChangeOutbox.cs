namespace AlertApi.Application.Interfaces;

public interface IConfigChangeOutbox
{
    // Tracks the record only: it is saved with the change itself by IUnitOfWork, in one transaction.
    void Add(string kind, string id, DateTimeOffset updatedAt, object body);
}
