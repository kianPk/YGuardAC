using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;

namespace YGuardAC;

internal static class MatchAbort
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static int _abortStarted;

    public static bool AlreadyStarted => Volatile.Read(ref _abortStarted) == 1;

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
        float delay = Math.Clamp(config.Actions.AbortDelaySeconds, 1f, 30f);

        if (cancel)
        {
            _ = Task.Run(async () =>
            {
                try { await CancelCurrentMatchAsync(config); }
                catch (Exception ex) { Console.WriteLine($"[YGuardAC] CancelMatch failed: {ex.Message}"); }
            });
        }

        if (quit)
        {
            scheduleOnMainThread(delay, () =>
            {
                Console.WriteLine("[YGuardAC] Quitting server after cheat abort.");
                Server.ExecuteCommand("quit");
            });
        }
    }

    private static async Task CancelCurrentMatchAsync(YGuardACConfig config)
    {
        string? matchId = await TryGetCurrentMatchIdAsync();
        if (string.IsNullOrWhiteSpace(matchId))
        {
            Console.WriteLine("[YGuardAC] No current match id — cannot cancel on panel.");
            return;
        }

        string? secret = FirstNonEmpty(
            config.Actions.HasuraAdminSecret,
            Environment.GetEnvironmentVariable("HASURA_GRAPHQL_ADMIN_SECRET"),
            Environment.GetEnvironmentVariable("HASURA_ADMIN_SECRET"));

        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.WriteLine("[YGuardAC] No Hasura admin secret — set Actions.HasuraAdminSecret or env HASURA_GRAPHQL_ADMIN_SECRET. Server will still quit.");
            return;
        }

        string? gqlUrl = FirstNonEmpty(
            config.Actions.GraphqlUrl,
            Environment.GetEnvironmentVariable("HASURA_GRAPHQL_ENDPOINT"),
            BuildDefaultGraphqlUrl());

        if (string.IsNullOrWhiteSpace(gqlUrl))
        {
            Console.WriteLine("[YGuardAC] No GraphQL URL.");
            return;
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

        using var req = new HttpRequestMessage(HttpMethod.Post, gqlUrl);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        req.Headers.Add("x-hasura-admin-secret", secret);

        using var resp = await Http.SendAsync(req);
        string respBody = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"[YGuardAC] CancelMatch {matchId} HTTP {(int)resp.StatusCode}: {respBody}");
    }

    private static async Task<string?> TryGetCurrentMatchIdAsync()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("MATCH_ID");
        string? serverId = Environment.GetEnvironmentVariable("SERVER_ID");
        string? apiPassword = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
        string? api = Environment.GetEnvironmentVariable("API_DOMAIN");

        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(apiPassword) || string.IsNullOrWhiteSpace(api))
            return fromEnv;

        string baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? api.TrimEnd('/') : $"https://{api.TrimEnd('/')}";
        string url = $"{baseUrl}/matches/current-match/{serverId}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiPassword);

        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[YGuardAC] current-match HTTP {(int)resp.StatusCode}");
            return fromEnv;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("id", out var idProp))
            return idProp.GetString() ?? fromEnv;

        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("match", out var match) &&
            match.TryGetProperty("id", out var nested))
            return nested.GetString() ?? fromEnv;

        return fromEnv;
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
