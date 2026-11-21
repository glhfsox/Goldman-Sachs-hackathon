using AlertApi.Domain.Models;

namespace AlertApi.Domain.Repositories;

public interface IRunbookStartRepository
{
    Task<RunbookStart?> GetAsync(string ruleId, string runbookId, CancellationToken cancellationToken = default);
}
