using System.Text.Json;
using System.Text.Json.Nodes;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.API.Infrastructure;

/// <summary>
/// JSON merge patch (RFC 7396): members in the patch replace the target's, null removes them, and members left out
/// keep their current values.
/// </summary>
internal static class MergePatch
{
    public const string MediaType = "application/merge-patch+json";

    /// <summary>Applies <paramref name="patch"/> to <paramref name="current"/>, both as their JSON form.</summary>
    /// <exception cref="JsonException">The patched document doesn't fit <typeparamref name="T"/>.</exception>
    public static T Apply<T>(T current, JsonElement patch)
    {
        var target = JsonSerializer.SerializeToNode(current, Signal9Json.Options);
        var merged = Merge(target, JsonNode.Parse(patch.GetRawText()));
        return merged.Deserialize<T>(Signal9Json.Options)
            ?? throw new JsonException("The patch removed the whole resource.");
    }

    private static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
            return patch;

        var result = target as JsonObject ?? [];
        foreach (var (name, value) in patchObject.ToList())
        {
            if (value is null)
                result.Remove(name);
            else
                result[name] = Merge(result[name]?.DeepClone(), value.DeepClone());
        }
        return result;
    }
}
