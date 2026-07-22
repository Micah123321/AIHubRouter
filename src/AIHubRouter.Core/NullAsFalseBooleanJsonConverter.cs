using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHubRouter.Core;

public sealed class NullAsFalseBooleanJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
            _ => throw new JsonException("available 必须是布尔值、布尔字符串或 null。")
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
