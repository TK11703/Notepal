using System.Data;
using Dapper;

namespace Notepal.Api.Data;

internal static class DapperConfiguration
{
    public static void Apply()
    {
        // Maps snake_case columns (owner_id) to PascalCase properties (OwnerId).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    /// <summary>Npgsql reads <c>timestamptz</c> as a UTC <see cref="DateTime"/>, which Dapper can't convert on its own.</summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value.ToUniversalTime();

        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot convert {value.GetType()} to {nameof(DateTimeOffset)}."),
        };
    }
}
