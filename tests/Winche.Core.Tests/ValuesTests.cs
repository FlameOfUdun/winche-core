using System.Text.Json;
using Winche.Values;

namespace Winche.Core.Tests;

public class ValuesTests
{
    [Fact]
    public void Plain_objects_become_values()
    {
        var v = Value.FromObject(new Dictionary<string, object?>
        {
            ["n"] = 3, ["d"] = 1.5, ["s"] = "x", ["b"] = true, ["none"] = null, ["list"] = new object?[] { 1L, "a" },
            ["at"] = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
        });
        var map = Assert.IsType<MapValue>(v).Fields;
        Assert.Equal(new IntegerValue(3), map["n"]);
        Assert.Equal(new DoubleValue(1.5), map["d"]);
        Assert.Equal(new StringValue("x"), map["s"]);
        Assert.Equal(new BooleanValue(true), map["b"]);
        Assert.IsType<NullValue>(map["none"]);
        Assert.Equal(new ArrayValue([new IntegerValue(1), new StringValue("a")]), map["list"]);
        Assert.IsType<TimestampValue>(map["at"]);
    }

    [Fact]
    public void Values_have_a_plain_json_shape_and_a_typed_wire_shape()
    {
        var doc = new MapValue(new Dictionary<string, Value>
        {
            ["where"] = new GeoPointValue(51.5, -0.1), ["ref"] = new ReferenceValue("users/u1"), ["n"] = new IntegerValue(7),
        });
        Assert.Equal("""{"where":{"latitude":51.5,"longitude":-0.1},"ref":"users/u1","n":7}""", PlainJson.Of(doc)!.ToJsonString());
        var wire = JsonSerializer.Serialize<Value>(doc);
        Assert.Equal(doc, JsonSerializer.Deserialize<Value>(wire));
        Assert.Equal(0, ValueComparer.Instance.Compare(new IntegerValue(1), new DoubleValue(1.0)));
    }

    [Fact]
    public void Plain_json_becomes_values()
    {
        var v = PlainJson.ToValue(System.Text.Json.Nodes.JsonNode.Parse("""{"n":3,"d":1.5,"s":"x","b":false,"none":null,"list":[1,"a"]}"""));
        var map = Assert.IsType<MapValue>(v).Fields;
        Assert.Equal(new IntegerValue(3), map["n"]);
        Assert.Equal(new DoubleValue(1.5), map["d"]);
        Assert.Equal(new BooleanValue(false), map["b"]);
        Assert.IsType<NullValue>(map["none"]);
        Assert.Equal(new ArrayValue([new IntegerValue(1), new StringValue("a")]), map["list"]);
        Assert.Equal(v, Value.FromObject(JsonDocument.Parse("""{"n":3,"d":1.5,"s":"x","b":false,"none":null,"list":[1,"a"]}""").RootElement));
    }

    [Fact]
    public void Numbers_made_in_code_convert_like_parsed_ones()
    {
        var made = new System.Text.Json.Nodes.JsonObject { ["i"] = 1, ["s"] = (short)2, ["m"] = 2.5m, ["f"] = 1.5f, ["big"] = 3_000_000_000L, ["u"] = 7u };
        var v = (MapValue)PlainJson.ToValue(made);
        Assert.Equal(new IntegerValue(1), v.Fields["i"]);
        Assert.Equal(new IntegerValue(2), v.Fields["s"]);
        Assert.Equal(new DoubleValue(2.5), v.Fields["m"]);
        Assert.Equal(new DoubleValue(1.5), v.Fields["f"]);
        Assert.Equal(new IntegerValue(3_000_000_000L), v.Fields["big"]);
        Assert.Equal(new IntegerValue(7), v.Fields["u"]);
        Assert.Equal(PlainJson.ToValue(System.Text.Json.Nodes.JsonNode.Parse(made.ToJsonString())), v);
    }

    [Fact]
    public void Document_file_and_user_references_have_their_own_tags()
    {
        Assert.Equal("""{"docRefValue":"patients/p1"}""", ValueSerializer.Write(new ReferenceValue("patients/p1")).ToJsonString());
        Assert.Equal("""{"fileRefValue":"scans/a.png"}""", ValueSerializer.Write(new FileRefValue("scans/a.png")).ToJsonString());
        Assert.Equal("""{"userRefValue":"u1"}""", ValueSerializer.Write(new UserRefValue("u1")).ToJsonString());
        Assert.Equal(new FileRefValue("a"), ValueSerializer.Read(System.Text.Json.Nodes.JsonNode.Parse("""{"fileRefValue":"a"}""")!));
        // Ordered by type, then value: documents, then files, then users (between bytes and geo points).
        Assert.True(ValueComparer.Instance.Compare(new ReferenceValue("z"), new FileRefValue("a")) < 0);
        Assert.True(ValueComparer.Instance.Compare(new FileRefValue("z"), new UserRefValue("a")) < 0);
        Assert.True(ValueComparer.Instance.Compare(new UserRefValue("z"), new GeoPointValue(0, 0)) < 0);
    }
}
