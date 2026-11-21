namespace Notifier.Application.Services;

public static class Time
{
    // Postgres timestamptz needs UTC kinds; a producer that drops the offset is treated as UTC.
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
