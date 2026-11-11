using MongoDB.Bson;

namespace Shoreline.Api.Infrastructure;

public static class BsonApi
{
    public static object? Value(BsonValue value) => value.BsonType switch
    {
        BsonType.Null => null,
        BsonType.Document => value.AsBsonDocument.ToDictionary(v => v.Name, v => Value(v.Value), StringComparer.Ordinal),
        BsonType.Array => value.AsBsonArray.Select(Value).ToArray(),
        BsonType.String => value.AsString,
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => Decimal128.ToDecimal(value.AsDecimal128),
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => Formats.PythonValue.Iso(value.AsBsonDateTime),
        _ => throw new InvalidOperationException("Unsupported API BSON type")
    };
}
