using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// The agent's JSON settings: <see cref="Signal9Json"/>'s, except that an enum value the agent doesn't know, which a
/// newer api may send, reads as an undefined value instead of failing the whole response. The agent then treats it as
/// unsupported, such as a command type it can't run.
/// </summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new TolerantEnumConverterFactory());
        return Signal9Json.Configure(options);
    }

    private sealed class TolerantEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    /// <summary>Reads and writes members by name, as <see cref="Signal9Json"/> does, and unknown names as undefined.</summary>
    private sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        /// <summary>A value no member has: below the smallest, so it never collides with one.</summary>
        private static readonly TEnum Undefined = (TEnum)Enum.ToObject(typeof(TEnum),
            Enum.GetValues<TEnum>().Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Min() - 1);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value)
                    ? value
                    : Undefined,
                JsonTokenType.Number => (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64()),
                _ => throw new JsonException($"Expected a {typeof(TEnum).Name} name."),
            };

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
