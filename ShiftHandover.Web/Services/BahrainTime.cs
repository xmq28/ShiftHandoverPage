using System.Globalization;

namespace ShiftHandover.Web.Services;

public static class BahrainTime
{
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(3);

    public static DateTimeOffset FromLocalInput(DateTimeOffset value) =>
        new(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified), UtcOffset);

    public static DateTimeOffset ToLocal(DateTimeOffset value) => value.ToOffset(UtcOffset);

    public static DateOnly GetLocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(ToLocal(value).DateTime);

    public static string Format(DateTimeOffset value) =>
        ToLocal(value).ToString("dd MMM yyyy, HH:mm 'Bahrain (UTC+03:00)'", CultureInfo.InvariantCulture);
}