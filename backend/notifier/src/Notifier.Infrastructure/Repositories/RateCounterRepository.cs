using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class RateCounterRepository : IRateCounterRepository
{
    private const string InsertSql = """
        INSERT INTO rate_counters (user_id, window_start, push_count, folded_count)
        VALUES ({0}, {1}, 0, 0)
        ON CONFLICT (user_id) DO NOTHING
        """;

    private const string LockSql = """
        SELECT user_id, window_start, push_count, folded_count
        FROM rate_counters
        WHERE user_id = {0}
        FOR UPDATE
        """;

    private readonly NotifierDbContext _context;

    public RateCounterRepository(NotifierDbContext context)
    {
        _context = context;
    }

    // The row lock lives in a transaction that NotifierDbContext.SaveChangesAsync commits.
    public async Task<RateCounter> LockAsync(string userId, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (_context.Database.CurrentTransaction is null)
        {
            await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        var fresh = RateCounter.Create(userId, now);
        await _context.Database.ExecuteSqlRawAsync(InsertSql, [userId, fresh.WindowStart], cancellationToken);

        var counters = await _context.RateCounters.FromSqlRaw(LockSql, userId).ToListAsync(cancellationToken);

        return counters.Single();
    }
}
