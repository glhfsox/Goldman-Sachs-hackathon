using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    // Row locks are the lease: held until SaveChangesAsync commits the outcome, released at once if the worker dies.
    private const string ClaimPendingSql = """
        SELECT id, sequence, topic, key, payload, created_at, processed_at, attempts, error, locked_until
        FROM outbox
        WHERE processed_at IS NULL
        ORDER BY sequence
        LIMIT {0}
        FOR UPDATE SKIP LOCKED
        """;

    private readonly NotifierDbContext _context;

    public OutboxRepository(NotifierDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await _context.OutboxMessages.AddAsync(message, cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        DateTime now,
        int batchSize,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        _ = now;
        _ = leaseUntil;

        if (_context.Database.CurrentTransaction is null)
        {
            await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        return await _context.OutboxMessages
            .FromSqlRaw(ClaimPendingSql, batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> DeleteProcessedBeforeAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        return await _context.OutboxMessages
            .Where(message => message.ProcessedAt != null && message.ProcessedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
