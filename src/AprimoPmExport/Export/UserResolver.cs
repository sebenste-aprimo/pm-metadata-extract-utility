using System.Text.Json;
using AprimoPmExport.Http;

namespace AprimoPmExport.Export;

/// <summary>
/// Turns user IDs into names by asking <c>GET /users/{id}</c>.
///
/// <para><b>Why not the lookup list.</b> The lookup behind <c>projectManager</c>
/// (<c>GET /lookup/506</c>) cannot do this job. Probed against a live tenant, it holds 600
/// entries sorted alphabetically from <c>--</c> to about "Cr", so everyone from roughly D to
/// Z is absent; and it holds active users only, so every departed person is absent too.
/// <c>/users/{id}</c> resolved all 37 of 37 users tried, inactive ones included.</para>
///
/// <para><b>What it costs.</b> One request per <i>distinct</i> user, cached for the run and
/// shared across every object, so a manager who appears on ten thousand projects is fetched
/// once. The first 1,000 projects had 144 distinct managers, so a full export is a few
/// thousand requests at most and a daily delta is a handful.</para>
///
/// <para><b>What it keeps.</b> <c>/users/{id}</c> returns a full user record — email, phone,
/// postal address, employee number, cost centre, login ID. Only a formatted name is retained;
/// the rest of each record is dropped as soon as the name has been read, is never logged, and
/// never reaches the CSV.</para>
///
/// <para>Resolution is split into an asynchronous <see cref="PrefetchAsync"/> and a synchronous
/// <see cref="TryGet"/> because row extraction is synchronous: the runner prefetches the users
/// on a page, then extracts the page against the cache.</para>
/// </summary>
public sealed class UserResolver
{
    private readonly Func<string, CancellationToken, Task<JsonDocument?>> _fetch;
    private readonly Action<string> _log;

    /// <summary>User ID → formatted name, or null when the user could not be resolved.</summary>
    private readonly Dictionary<string, string?> _names = new(StringComparer.Ordinal);

    /// <summary>How many individual failures to describe in the log before going quiet.</summary>
    private const int FailuresLogged = 5;

    /// <summary>Requests actually sent. Cache hits are free and not counted.</summary>
    public int Requests { get; private set; }

    /// <summary>Users whose request failed for a reason other than "not found".</summary>
    public int Failures { get; private set; }

    public int Resolved => _names.Values.Count(v => v is not null);
    public int Unresolved => _names.Count - Resolved;

    /// <param name="fetch">
    /// Returns the user's JSON, or null when the user does not exist or cannot be read.
    /// Injected so the caching and failure handling can be tested without a tenant.
    /// </param>
    public UserResolver(Func<string, CancellationToken, Task<JsonDocument?>> fetch, Action<string> log)
    {
        _fetch = fetch;
        _log = log;
    }

    /// <summary>Builds a resolver that reads users through the API client.</summary>
    public static UserResolver FromClient(ApiClient client, Action<string> log) =>
        new((id, cancellationToken) => client.TryGetJsonAsync(
                $"/users/{id}", $"user {id}", cancellationToken), log);

    /// <summary>
    /// Fetches every user in <paramref name="codes"/> that has not been seen yet.
    ///
    /// <para>A value that is not all digits is ignored rather than sent: IDs go into a URL
    /// path, and a non-numeric value here means the column holds something other than a user
    /// ID (an object, say), which no request could resolve.</para>
    ///
    /// <para>A failed request leaves that one user unresolved and the run going. Losing a name
    /// is recoverable — the cell keeps the numeric code — while aborting a multi-hour export
    /// over one unreadable user is not.</para>
    /// </summary>
    public async Task PrefetchAsync(IEnumerable<string> codes, CancellationToken cancellationToken)
    {
        foreach (var code in codes.Distinct(StringComparer.Ordinal))
        {
            if (code.Length == 0 || !code.All(char.IsAsciiDigit)) continue;
            if (_names.ContainsKey(code)) continue;

            cancellationToken.ThrowIfCancellationRequested();

            Requests++;

            try
            {
                using var document = await _fetch(code, cancellationToken).ConfigureAwait(false);

                // Only the name survives this line. The document, and everything personal in
                // it, is disposed on leaving the block.
                _names[code] = document is null ? null : FormatName(document.RootElement);
            }
            catch (ApiException ex)
            {
                Failures++;
                _names[code] = null;

                if (Failures <= FailuresLogged)
                    _log($"  Could not read user {code} ({ex.Message.Split('\n')[0]}). " +
                         "Its cells keep the numeric code." +
                         (Failures == FailuresLogged ? " Further failures will not be listed." : ""));
            }
        }
    }

    /// <summary>The cached name for a user, or false when it was never fetched or did not resolve.</summary>
    public bool TryGet(string code, out string name)
    {
        name = "";
        if (!_names.TryGetValue(code, out var cached) || cached is null) return false;

        name = cached;
        return true;
    }

    /// <summary>
    /// Formats a user as <c>Last, First</c>, matching the labels the lookup list already
    /// produces — so rows that resolved either way read the same, and switching routes does
    /// not change what a downstream consumer sees.
    ///
    /// <para>Falls back to whichever part exists. Real records need it: the authenticated
    /// service user comes back with its entire name in <c>lastName</c> and no
    /// <c>firstName</c>. Returns null when there is no name at all, so the cell keeps the code
    /// instead of reading as blank.</para>
    /// </summary>
    internal static string? FormatName(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object) return null;

        string Read(string property) =>
            user.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "").Trim()
                : "";

        var last = Read("lastName");
        var first = Read("firstName");

        if (last.Length > 0 && first.Length > 0) return $"{last}, {first}";
        if (last.Length > 0) return last;
        return first.Length > 0 ? first : null;
    }
}
