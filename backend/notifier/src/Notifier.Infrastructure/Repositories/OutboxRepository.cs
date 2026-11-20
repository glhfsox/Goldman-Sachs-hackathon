using Microsoft.EntityFrameworkCore;

using Notifier.Domain.Models;
using Notifier.Domain.Repositories;
using Notifier.Infrastructure.Data;

namespace Notifier.Infrastructure.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private const string ClaimPendingSql = """
        UPDATE outbox
        SET locked_until = {2}
        WHERE id IN (
            SELECT id
            FROM outbox
            WHERE processed_at IS NULL
              AND (locked_until IS NULL OR locked_until <= {0})
            ORDER BY sequence
            LIMIT {1}
            FOR UPDATE SKIP LOCKED)
        RETURNING id, sequence, topic, key, payload, created_at, processed_at, attempts, error, locked_until
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
        return await _context.OutboxMessages
            .FromSqlRaw(ClaimPendingSql, now, batchSize, leaseUntil)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
