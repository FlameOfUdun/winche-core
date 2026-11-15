using System.Text.Json.Nodes;

namespace Winche.Values;

/// <summary>
/// Values as plain JSON, for webhook receivers, functions and logs: numbers, strings, timestamps as ISO text, references
/// as paths, bytes as base64, geopoints as <c>{latitude, longitude}</c>.
/// </summary>
public static class PlainJson
{
    /// <summary>The plain JSON for <paramref name="value"/>.</summary>
    public static JsonNode? Of(Value value) => value switch
    {
        NullValue => null,
        BooleanValue b => b.Value,
        IntegerValue i => i.Value,
        DoubleValue d => double.IsFinite(d.Value) ? d.Value : d.Value.ToString(),
        StringValue s => s.Value,
        TimestampValue t => t.Value.ToString("O"),
        BytesValue b => Convert.ToBase64String(b.Value),
        ReferenceValue r => r.Path,
        FileRefValue f => f.Path,
        UserRefValue u => u.Uid,
        GeoPointValue g => new JsonObject { ["latitude"] = g.Latitude, ["longitude"] = g.Longitude },
        ArrayValue a => new JsonArray([.. a.Values.Select(Of)]),
        MapValue m => Fields(m.Fields),
        _ => value.ToString(),
    };

    /// <summary>A map of values as a plain JSON object.</summary>
    public static JsonObject Fields(IReadOnlyDictionary<string, Value> fields) =>
        new(fields.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Of(kv.Value))));

    /// <summary>
    /// The value for plain JSON: whole numbers as integers, other numbers as doubles, objects as maps, arrays as
    /// arrays. (Text stays text: plain JSON can't say a string is a timestamp or a path.)
    /// </summary>
    public static Value ToValue(JsonNode? node) => node switch
    {
        null => new NullValue(),
        JsonObject obj => new MapValue(obj.ToDictionary(kv => kv.Key, kv => ToValue(kv.Value), StringComparer.Ordinal)),
        JsonArray arr => new ArrayValue([.. arr.Select(ToValue)]),
        JsonValue v => v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => new BooleanValue(true),
            System.Text.Json.JsonValueKind.False => new BooleanValue(false),
            System.Text.Json.JsonValueKind.String => new StringValue(v.GetValue<string>()),
            System.Text.Json.JsonValueKind.Number => Number(v),
            _ => new NullValue(),
        },
        _ => new StringValue(node.ToJsonString()),
    };

    /// <summary>
    /// A number, parsed (a JsonElement inside) or made in code (an int, decimal, float… inside, which
    /// <c>GetValue&lt;long&gt;</c> won't convert): whole numbers in range become integers, the rest doubles.
    /// </summary>
    private static Value Number(JsonValue v)
    {
        if (v.TryGetValue<long>(out var l)) return new IntegerValue(l);
        if (v.TryGetValue<int>(out var i)) return new IntegerValue(i);
        if (v.TryGetValue<double>(out var d)) return new DoubleValue(d);
        var e = System.Text.Json.JsonDocument.Parse(v.ToJsonString()).RootElement;
        return e.TryGetInt64(out var whole) ? new IntegerValue(whole) : new DoubleValue(e.GetDouble());
    }
}
