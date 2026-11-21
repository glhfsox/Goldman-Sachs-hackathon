using AlertApi.Application.DTOs.Responses;

namespace AlertApi.Application.Interfaces;

public interface IAlertChangeOutbox
{
    // Tracks the alerts.changes record only: it is saved with IUnitOfWork, like IConfigChangeOutbox.
    void Add(string changeId, string change, DateTimeOffset at, string actor, AlertResponse alert);
}
