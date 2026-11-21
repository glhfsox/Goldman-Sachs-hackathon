using Microsoft.EntityFrameworkCore;

using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Queries;

public sealed class AlertTeamLookup : IAlertTeamLookup
{
    private readonly ApiDbContext _context;

    public AlertTeamLookup(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<string?> FindTeamAsync(string alertId, CancellationToken cancellationToken = default)
    {
        return await _context.Alerts
            .AsNoTracking()
            .Where(alert => alert.Id == alertId)
            .Select(alert => alert.TeamId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
