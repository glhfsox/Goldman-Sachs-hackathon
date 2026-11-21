using Microsoft.EntityFrameworkCore;

using AlertApi.Application.DTOs.Responses;
using AlertApi.Application.Interfaces;
using AlertApi.Infrastructure.Data;

namespace AlertApi.Infrastructure.Queries;

public sealed class ReferenceDataQueries : IReferenceDataQueries
{
    private readonly ApiDbContext _context;

    public ReferenceDataQueries(ApiDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<TeamResponse>> GetTeamsAsync(CancellationToken cancellationToken = default)
    {
        var teams = await _context.Teams.AsNoTracking().OrderBy(team => team.Id).ToListAsync(cancellationToken);
        var members = await (
            from membership in _context.Memberships.AsNoTracking()
            join user in _context.Users.AsNoTracking() on membership.UserId equals user.Id
            orderby user.Id
            select new { membership.TeamId, Member = new TeamMemberResponse(user.Id, user.Name, membership.OnCall) })
            .ToListAsync(cancellationToken);

        return teams
            .Select(team => new TeamResponse(
                team.Id,
                team.Name,
                members.Where(member => member.TeamId == team.Id).Select(member => member.Member).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<UserResponse>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Users
            .AsNoTracking()
            .OrderBy(user => user.Id)
            .Select(user => new UserResponse(user.Id, user.Name, user.Email, user.Phone))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> UserExistsAsync(string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Users.AnyAsync(user => user.Id == userId, cancellationToken);
    }
}
