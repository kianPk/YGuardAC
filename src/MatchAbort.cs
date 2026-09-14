using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;

namespace YGuardAC;

internal static class MatchAbort
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static int _abortStarted;
    private static string? _cachedMatchId;
    private static readonly object CacheLock = new();

    public static bool AlreadyStarted => Volatile.Read(ref _abortStarted) == 1;

    public static void RememberMatchId(string? matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return;
        lock (CacheLock) _cachedMatchId = matchId.Trim();
    }

    public static string? GetCachedMatchId()
    {
        lock (CacheLock) return _cachedMatchId;
    }

    public static void Begin(YGuardACConfig config, string reason, Action<float, Action> scheduleOnMainThread)
    {
        if (Interlocked.Exchange(ref _abortStarted, 1) == 1)
            return;

        Console.WriteLine($"[YGuardAC] Aborting match: {reason}");

        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot) continue;
            p.PrintToChat($" \x02[YGuardAC]\x01 Match cancelled — cheat detected ({reason})");
        }

        bool cancel = config.Actions.CancelMatchOnCheat;
        bool quit = config.Actions.QuitServerOnCheat;
        float delayAfterCancel = Math.Clamp(config.Actions.AbortDelaySeconds, 2f, 30f);

        _ = Task.Run(async () =>
        {
            try
            {
                if (cancel)
                    await CancelCurrentMatchAsync(config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YGuardAC] CancelMatch failed: {ex.Message}");
            }
            finally
            {
                if (quit)
                {
                    // Quit only AFTER cancel attempt finishes.
                    Server.NextFrame(() =>
                    {
                        scheduleOnMainThread(delayAfterCancel, () =>
                        {
                            Console.WriteLine("[YGuardAC] Quitting server after cheat abort.");
                            Server.ExecuteCommand("quit");
                        });
                    });
                }
            }
        });
    }

    public static async Task RefreshMatchIdCacheAsync()
    {
        try
        {
            string? id = await TryGetCurrentMatchIdAsync();
            if (!string.IsNullOrWhiteSpace(id))
                RememberMatchId(id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YGuardAC] match-id refresh failed: {ex.Message}");
        }
    }

    private static async Task CancelCurrentMatchAsync(YGuardACConfig config)
    {
        string? matchId = GetCachedMatchId() ?? await TryGetCurrentMatchIdAsync();
        if (string.IsNullOrWhiteSpace(matchId))
        {
            Console.WriteLine("[YGuardAC] No current match id — cannot cancel on panel.");
            return;
        }

        RememberMatchId(matchId);

        string? secret = FirstNonEmpty(
            config.Actions.HasuraAdminSecret,
            Environment.GetEnvironmentVariable("HASURA_GRAPHQL_ADMIN_SECRET"),
            Environment.GetEnvironmentVariable("HASURA_ADMIN_SECRET"));

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.WriteLine("[YGuardAC] No Hasura admin secret — set Actions.HasuraAdminSecret.");
            return;
        }

        var urls = new List<string>();
        void AddUrl(string? u)
        {
            if (!string.IsNullOrWhiteSpace(u) && !urls.Contains(u, StringComparer.OrdinalIgnoreCase))
                urls.Add(u.Trim().TrimEnd('/'));
        }

        AddUrl(config.Actions.GraphqlUrl);
        AddUrl(Environment.GetEnvironmentVariable("HASURA_GRAPHQL_ENDPOINT"));
        AddUrl(BuildDefaultGraphqlUrl());
        // Common self-host variants
        string? api = Environment.GetEnvironmentVariable("API_DOMAIN");
        if (!string.IsNullOrWhiteSpace(api))
        {
            string baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? api.TrimEnd('/') : $"https://{api.TrimEnd('/')}";
            AddUrl($"{baseUrl}/v1/graphql");
        }

        var body = new
        {
            query = @"mutation CancelByAc($id: uuid!) {
  update_matches_by_pk(pk_columns: { id: $id }, _set: { status: ""Canceled"" }) {
    id
    status
  }
}",
            variables = new { id = matchId }
        };
        string json = JsonSerializer.Serialize(body);

        foreach (var gqlUrl in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, gqlUrl);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                req.Headers.Add("x-hasura-admin-secret", secret);

                using var resp = await Http.SendAsync(req);
                string respBody = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[YGuardAC] CancelMatch {matchId} via {gqlUrl} => HTTP {(int)resp.StatusCode}: {respBody}");

                if (resp.IsSuccessStatusCode && respBody.Contains("Canceled", StringComparison.OrdinalIgnoreCase))
                    return;
                if (resp.IsSuccessStatusCode && respBody.Contains("\"status\"", StringComparison.Ordinal))
                    return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YGuardAC] Cancel via {gqlUrl} error: {ex.Message}");
            }
        }
    }

    private static async Task<string?> TryGetCurrentMatchIdAsync()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("MATCH_ID");
        string? serverId = Environment.GetEnvironmentVariable("SERVER_ID");
        string? apiPassword = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
        string? api = Environment.GetEnvironmentVariable("API_DOMAIN");

        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(apiPassword) || string.IsNullOrWhiteSpace(api))
            return fromEnv ?? GetCachedMatchId();

        string baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? api.TrimEnd('/') : $"https://{api.TrimEnd('/')}";
        string url = $"{baseUrl}/matches/current-match/{serverId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiPassword);

        using var resp = await Http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return fromEnv ?? GetCachedMatchId();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[YGuardAC] current-match HTTP {(int)resp.StatusCode}");
            return fromEnv ?? GetCachedMatchId();
        }

        string text = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
            return fromEnv ?? GetCachedMatchId();

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("id", out var idProp))
            return idProp.GetString() ?? fromEnv ?? GetCachedMatchId();

        return fromEnv ?? GetCachedMatchId();
    }

    private static string? BuildDefaultGraphqlUrl()
    {
        string? api = Environment.GetEnvironmentVariable("API_DOMAIN");
        if (string.IsNullOrWhiteSpace(api)) return null;
        string baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? api.TrimEnd('/') : $"https://{api.TrimEnd('/')}";
        return $"{baseUrl}/v1/graphql";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }
}
