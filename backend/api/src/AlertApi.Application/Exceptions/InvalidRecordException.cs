namespace AlertApi.Application.Exceptions;

// A Kafka record that can never be applied (bad JSON, missing field). The consumer skips it; retrying would not help.
public sealed class InvalidRecordException : Exception
{
    public InvalidRecordException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
