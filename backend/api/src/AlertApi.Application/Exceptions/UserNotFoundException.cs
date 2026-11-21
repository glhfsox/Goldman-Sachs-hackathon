namespace AlertApi.Application.Exceptions;

public sealed class UserNotFoundException : NotFoundException
{
    public UserNotFoundException(string id)
        : base($"User with id '{id}' was not found.")
    {
    }
}
