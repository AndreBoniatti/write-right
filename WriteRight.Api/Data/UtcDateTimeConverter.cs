using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WriteRight.Api.Data;

/// <summary>
/// Grava <see cref="DateTimeOffset"/> como <see cref="DateTime"/> em UTC. Todo instante
/// do app nasce de <c>UtcNow</c>, então o offset não carrega informação.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.UtcDateTime,
            v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)))
    {
    }
}
