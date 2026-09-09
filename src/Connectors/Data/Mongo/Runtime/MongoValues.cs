using System.Globalization;
using Koan.Data.Abstractions;
using MongoDB.Bson;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.Mongo.Runtime;

internal static class MongoValues
{
    public static string Path(PhysicalPath path) =>
        path.IsNested ? $"{path.Name}.{string.Join('.', path.Segments)}" : path.Name;

    public static BsonValue FromJson(JToken? token) => token?.Type switch
    {
        null or JTokenType.Null or JTokenType.Undefined => BsonNull.Value,
        JTokenType.Object => new BsonDocument(((JObject)token).Properties()
            .Select(property => new BsonElement(property.Name, FromJson(property.Value)))),
        JTokenType.Array => new BsonArray(((JArray)token).Select(FromJson)),
        JTokenType.Boolean => new BsonBoolean(token.Value<bool>()),
        JTokenType.Integer => Integer((JValue)token),
        JTokenType.Float => Number((JValue)token),
        JTokenType.Bytes => new BsonBinaryData(token.Value<byte[]>()!),
        JTokenType.Guid => new BsonString(token.Value<Guid>().ToString("D")),
        JTokenType.Date => new BsonDateTime(token.Value<DateTime>().ToUniversalTime()),
        _ => new BsonString(token.Value<string>() ?? token.ToString())
    };

    public static JToken ToJson(BsonValue value)
    {
        if (value.IsBsonNull) return JValue.CreateNull();
        return value.BsonType switch
        {
            BsonType.Document => new JObject(value.AsBsonDocument.Select(element =>
                new JProperty(element.Name, ToJson(element.Value)))),
            BsonType.Array => new JArray(value.AsBsonArray.Select(ToJson)),
            BsonType.Boolean => new JValue(value.AsBoolean),
            BsonType.Int32 => new JValue(value.AsInt32),
            BsonType.Int64 => new JValue(value.AsInt64),
            BsonType.Double => new JValue(value.AsDouble),
            BsonType.Decimal128 => new JValue(value.AsDecimal128.ToString()),
            BsonType.Binary => new JValue(value.AsBsonBinaryData.Bytes),
            BsonType.DateTime => new JValue(value.ToUniversalTime()),
            BsonType.ObjectId => new JValue(value.AsObjectId.ToString()),
            BsonType.Timestamp => new JValue(value.AsBsonTimestamp.Value),
            BsonType.RegularExpression => new JValue(value.AsBsonRegularExpression.Pattern),
            _ => new JValue(value.ToString())
        };
    }

    public static BsonValue FromNeutral(object? value) => value switch
    {
        null => BsonNull.Value,
        BsonValue bson => bson,
        JToken token => FromJson(token),
        DataObject data => new BsonDocument(data.Properties.Select(property =>
            new BsonElement(property.Name, FromNeutral(property.Value)))),
        DataArray data => new BsonArray(data.Items.Select(FromNeutral)),
        string text => new BsonString(text),
        char character => new BsonString(character.ToString()),
        bool flag => new BsonBoolean(flag),
        byte number => new BsonInt32(number),
        sbyte number => new BsonInt32(number),
        short number => new BsonInt32(number),
        ushort number => new BsonInt32(number),
        int number => new BsonInt32(number),
        uint number when number <= int.MaxValue => new BsonInt32((int)number),
        uint number => new BsonInt64(number),
        long number => new BsonInt64(number),
        ulong number when number <= long.MaxValue => new BsonInt64((long)number),
        ulong number => new BsonString(number.ToString(CultureInfo.InvariantCulture)),
        float number => new BsonDouble(number),
        double number => new BsonDouble(number),
        decimal number => new BsonDecimal128(number),
        Guid guid => new BsonString(guid.ToString("D")),
        DateTime dateTime => new BsonDateTime(dateTime.ToUniversalTime()),
        DateTimeOffset dateTimeOffset => new BsonDateTime(dateTimeOffset.UtcDateTime),
        DateOnly dateOnly => new BsonString(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly timeOnly => new BsonString(timeOnly.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
        TimeSpan timeSpan => new BsonInt64(timeSpan.Ticks),
        byte[] bytes => new BsonBinaryData(bytes),
        Enum enumeration => new BsonString(EnumStorageEncoding.Format(enumeration)),
        IEnumerable<object?> values => new BsonArray(values.Select(FromNeutral)),
        System.Collections.IEnumerable values => new BsonArray(values.Cast<object?>().Select(FromNeutral)),
        _ => BsonValue.Create(value)
    };

    public static object? ToNeutral(BsonValue value)
    {
        if (value.IsBsonNull) return null;
        return value.BsonType switch
        {
            BsonType.Document => new DataObject(value.AsBsonDocument.Select(element =>
                new DataProperty(element.Name, ToNeutral(element.Value)))),
            BsonType.Array => new DataArray(value.AsBsonArray.Select(ToNeutral)),
            BsonType.Boolean => value.AsBoolean,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.Decimal128 => Decimal(value.AsDecimal128),
            BsonType.String => value.AsString,
            BsonType.Binary => value.AsBsonBinaryData.Bytes.ToArray(),
            BsonType.DateTime => value.ToUniversalTime(),
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.Timestamp => value.AsBsonTimestamp.Value,
            BsonType.RegularExpression => value.AsBsonRegularExpression.Pattern,
            _ => value.ToString()
        };
    }

    public static void Set(BsonDocument document, PhysicalPath path, BsonValue value)
    {
        var current = document;
        foreach (var segment in path.Segments.Prepend(path.Name).SkipLast(1))
        {
            if (!current.TryGetValue(segment, out var nested) || !nested.IsBsonDocument)
            {
                var created = new BsonDocument();
                current[segment] = created;
                current = created;
            }
            else current = nested.AsBsonDocument;
        }
        current[path.Segments.Count == 0 ? path.Name : path.Segments[^1]] = value;
    }

    public static bool TryGet(BsonDocument document, PhysicalPath path, out BsonValue value)
    {
        BsonValue current = document;
        foreach (var segment in path.Segments.Prepend(path.Name))
        {
            if (!current.IsBsonDocument || !current.AsBsonDocument.TryGetValue(segment, out current!))
            {
                value = BsonNull.Value;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static BsonValue Integer(JValue value)
    {
        var raw = value.Value;
        return raw switch
        {
            int number => new BsonInt32(number),
            long number => new BsonInt64(number),
            uint number when number <= int.MaxValue => new BsonInt32((int)number),
            uint number => new BsonInt64(number),
            ulong number when number <= long.MaxValue => new BsonInt64((long)number),
            _ => new BsonInt64(Convert.ToInt64(raw, CultureInfo.InvariantCulture))
        };
    }

    private static BsonValue Number(JValue value) => value.Value switch
    {
        decimal number => new BsonDecimal128(number),
        float number => new BsonDouble(number),
        double number => new BsonDouble(number),
        var raw => new BsonDouble(Convert.ToDouble(raw, CultureInfo.InvariantCulture))
    };

    private static object Decimal(Decimal128 value) =>
        decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : value.ToString();
}
