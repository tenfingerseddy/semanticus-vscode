using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Semanticus.Engine
{
    /// <summary>The engine pipe is Newtonsoft (RpcServer.ConfigureSerializer) but the MCP door and a few DTOs
    /// (ObjectEditItem.Set) hold System.Text.Json elements. Newtonsoft does not know that
    /// struct, so every value crossed the pipe as an Undefined element and GetRawText() threw (hero16 N1: every
    /// edit_objects item refused). This converter carries the element as the JSON it is.</summary>
    internal sealed class JsonElementWire : JsonConverter
    {
        public override bool CanConvert(Type t) => t == typeof(System.Text.Json.JsonElement) || t == typeof(System.Text.Json.JsonElement?);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not System.Text.Json.JsonElement e || e.ValueKind == System.Text.Json.JsonValueKind.Undefined) { writer.WriteNull(); return; }
            writer.WriteRawValue(e.GetRawText());
        }

        public override object ReadJson(JsonReader reader, Type t, object existing, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return t == typeof(System.Text.Json.JsonElement?) ? null : System.Text.Json.JsonDocument.Parse("null").RootElement.Clone();
            var raw = JToken.Load(reader).ToString(Formatting.None);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
    }
}
