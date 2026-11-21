namespace AlertApi.Application.Interfaces;

public interface IActivityRecordHandler
{
    // Throws InvalidRecordException for a record that can never be applied; any other exception means "try again".
    Task HandleAsync(string topic, string value, CancellationToken cancellationToken = default);
}
