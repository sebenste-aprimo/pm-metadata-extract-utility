using System.Text.Json;

namespace AprimoPmExport.Export;

/// <summary>Why an envelope could not be read.</summary>
public enum HalEnvelopeStatus
{
    /// <summary>Items were found.</summary>
    Ok,

    /// <summary>A well-formed envelope describing an empty collection.</summary>
    Empty,

    /// <summary>No <c>_embedded</c> object at all, on a response that clearly has content.</summary>
    NotAnEnvelope,

    /// <summary>A configured <see cref="Configuration.PmObject.EmbedKey"/> matched nothing.</summary>
    EmbedKeyNotFound
}

public readonly record struct HalEnvelope(
    HalEnvelopeStatus Status,
    JsonElement Items,
    string? EmbedKey,
    long? Total,
    string? NextLink,
    IReadOnlyList<string> AvailableKeys,
    bool EmbedKeyMismatch = false)
{
    public bool HasItems => Status == HalEnvelopeStatus.Ok && Items.ValueKind == JsonValueKind.Array;

    public int Count => HasItems ? Items.GetArrayLength() : 0;
}

/// <summary>
/// Reads the HAL envelope every paged PM endpoint returns:
///
/// <code>
///   { "_total": 1234,
///     "_embedded": { "&lt;resourceTypeName&gt;": [ … ] },
///     "_links":    { "self": {...}, "next": {...}, … } }
/// </code>
///
/// <para>Separated from <see cref="HalPagedReader"/> so the envelope handling can be
/// tested against fixtures without an HTTP round trip — it is the part most likely to
/// meet a shape the spec did not describe.</para>
///
/// <para>The spec types <c>_embedded</c> as <c>additionalProperties</c> "keyed by the
/// resource type name" but never states that key for any object, so nothing here
/// hard-codes one: the sole array under <c>_embedded</c> is taken. A configured
/// <c>EmbedKey</c> switches on a strict lookup instead.</para>
/// </summary>
public static class HalEnvelopeReader
{
    public static HalEnvelope Read(JsonElement root, string? configuredEmbedKey)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new HalEnvelope(HalEnvelopeStatus.NotAnEnvelope, default, null, null, null,
                Array.Empty<string>());

        var total = ReadTotal(root);
        var next = ReadNextLink(root);

        if (!root.TryGetProperty("_embedded", out var embedded) ||
            embedded.ValueKind != JsonValueKind.Object)
        {
            // An empty collection may legitimately omit _embedded entirely, and a
            // _total of 0 says so unambiguously.
            var status = total is 0 || !root.EnumerateObject().Any()
                ? HalEnvelopeStatus.Empty
                : HalEnvelopeStatus.NotAnEnvelope;

            return new HalEnvelope(status, default, null, total, next, KeysOf(root));
        }

        var arrays = embedded.EnumerateObject()
                             .Where(p => p.Value.ValueKind == JsonValueKind.Array)
                             .ToList();

        if (!string.IsNullOrWhiteSpace(configuredEmbedKey))
        {
            foreach (var candidate in arrays)
            {
                if (!candidate.Name.Equals(configuredEmbedKey, StringComparison.OrdinalIgnoreCase)) continue;
                return new HalEnvelope(HalEnvelopeStatus.Ok, candidate.Value, candidate.Name, total, next,
                    arrays.Select(a => a.Name).ToList());
            }

            // The configured key is wrong. Whether that is fatal depends on whether
            // there is anything to be wrong *about*: with exactly one array there is no
            // ambiguity for the key to resolve, so take it and complain loudly. Failing
            // there would mean one stale key in the registry could kill an object that
            // the key-less path would have read perfectly well.
            //
            // With several arrays the key is the only thing distinguishing them, so a
            // miss really is unresolvable and has to stop.
            if (arrays.Count == 1)
                return new HalEnvelope(HalEnvelopeStatus.Ok, arrays[0].Value, arrays[0].Name, total, next,
                    arrays.Select(a => a.Name).ToList(), EmbedKeyMismatch: true);

            return new HalEnvelope(HalEnvelopeStatus.EmbedKeyNotFound, default, null, total, next,
                arrays.Select(a => a.Name).ToList());
        }

        if (arrays.Count == 0)
            return new HalEnvelope(HalEnvelopeStatus.Empty, default, null, total, next,
                KeysOf(embedded));

        return new HalEnvelope(HalEnvelopeStatus.Ok, arrays[0].Value, arrays[0].Name, total, next,
            arrays.Select(a => a.Name).ToList());
    }

    public static long? ReadTotal(JsonElement root) =>
        root.TryGetProperty("_total", out var total) &&
        total.ValueKind == JsonValueKind.Number &&
        total.TryGetInt64(out var parsed)
            ? parsed
            : null;

    /// <summary>Reads <c>_links.next.href</c>, if the envelope carries one.</summary>
    public static string? ReadNextLink(JsonElement root)
    {
        if (!root.TryGetProperty("_links", out var links) || links.ValueKind != JsonValueKind.Object)
            return null;
        if (!links.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Object)
            return null;
        if (!next.TryGetProperty("href", out var href) || href.ValueKind != JsonValueKind.String)
            return null;

        var value = href.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string DescribeKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString();
        var keys = KeysOf(root);
        return keys.Count == 0 ? "(none)" : string.Join(", ", keys.Take(12));
    }

    private static IReadOnlyList<string> KeysOf(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(p => p.Name).ToList()
            : Array.Empty<string>();
}
