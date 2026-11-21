namespace AlertApi.Application.Interfaces;

public interface IAlertTeamLookup
{
    Task<string?> FindTeamAsync(string alertId, CancellationToken cancellationToken = default);
}
