namespace OSI.Signal9.API.Infrastructure;

internal static class TimeExtensions
{
    public static DateTimeOffset AsUtcOffset(this DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static DateTimeOffset? AsUtcOffset(this DateTime? utc) => utc?.AsUtcOffset();
}
