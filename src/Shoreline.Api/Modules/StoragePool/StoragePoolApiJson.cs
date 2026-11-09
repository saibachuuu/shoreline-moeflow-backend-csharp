using MongoDB.Bson;

namespace Shoreline.Api.Modules.StoragePool;

/// <summary>
/// Converts the module's BSON views to ordinary CLR values before ASP.NET's
/// System.Text.Json serializer sees them. This keeps ObjectId values as strings
/// and avoids the MongoDB driver's BSON serialization leaking into HTTP.
/// </summary>
internal static class StoragePoolApiJson
{
    public static object? ToObject(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => ((BsonDocument)value).Elements.ToDictionary(
            element => element.Name, element => ToObject(element.Value), StringComparer.Ordinal),
        BsonType.Array => ((BsonArray)value).Select(ToObject).ToList(),
        BsonType.Null => null,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.DateTime => ((BsonDateTime)value).ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Decimal128 => value.ToDouble(),
        BsonType.String => value.AsString,
        _ => value.ToString(),
    };
}
