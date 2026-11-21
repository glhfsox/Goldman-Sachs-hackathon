using Notifier.Domain.Models;

namespace Notifier.Domain.Repositories;

public interface IRateCounterRepository
{
    // Returns the person's counter, created if missing, locked until the next save: parallel sends to one
    // person take turns, so none of them can read a stale push count.
    Task<RateCounter> LockAsync(string userId, DateTime now, CancellationToken cancellationToken = default);
}
