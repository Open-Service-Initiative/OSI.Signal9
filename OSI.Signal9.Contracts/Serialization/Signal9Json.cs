using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSI.Signal9.Contracts.Serialization;

/// <summary>
/// JSON settings every Signal9 client and the API agree on: web defaults (camelCase) and enums as names. A number, or
/// a number in a string, is refused, so a value no member has can't be read into an enum.
/// </summary>
public static class Signal9Json
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
