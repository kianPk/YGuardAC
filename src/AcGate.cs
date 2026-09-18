using System.Net.Http.Headers;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace YGuardAC;

/// <summary>
/// Asks the panel API whether a steam id may stay on this match server.
/// Auth: Bearer SERVER_API_PASSWORD (injected by 5Stack into the game pod).
/// </summary>
internal static class AcGate
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<(bool allowed, string reason)> CheckAsync(ulong steamId64)
    {
        string? serverId = Environment.GetEnvironmentVariable("SERVER_ID");
        string? apiPassword = Environment.GetEnvironmentVariable("SERVER_API_PASSWORD");
        string? api = Environment.GetEnvironmentVariable("API_DOMAIN");

        if (string.IsNullOrWhiteSpace(serverId) ||
            string.IsNullOrWhiteSpace(apiPassword) ||
            string.IsNullOrWhiteSpace(api))
        {
            // Not a 5Stack managed pod — don't block.
            return (true, "");
        }

        string baseUrl = api.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? api.TrimEnd('/')
            : $"https://{api.TrimEnd('/')}";
        string url =
            $"{baseUrl}/plugins/ac/server/check?server_id={Uri.EscapeDataString(serverId)}" +
            $"&steam_id={Uri.EscapeDataString(steamId64.ToString())}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiPassword);
            using var resp = await Http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[YGuardAC] AC gate HTTP {(int)resp.StatusCode}: {body}");
                // Fail open on auth/API errors so a panel outage doesn't empty the server.
                return (true, "");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool required = root.TryGetProperty("required", out var reqProp) && reqProp.GetBoolean();
            bool allowed = !root.TryGetProperty("allowed", out var okProp) || okProp.GetBoolean();
            string reason = root.TryGetProperty("reason", out var reasonProp)
                ? (reasonProp.GetString() ?? "")
                : "";

            if (!required) return (true, "");
            if (allowed) return (true, "");
            return (
                false,
                string.IsNullOrWhiteSpace(reason)
                    ? "YGuard AC: reopen the Anti-Cheat launcher"
                    : reason);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YGuardAC] AC gate error: {ex.Message}");
            return (true, "");
        }
    }

    public static void KickIfDenied(CCSPlayerController player, string reason)
    {
        if (player is null || !player.IsValid || player.IsBot) return;
        string safe = (reason ?? "YGuard AC").Replace("\"", "'").Trim();
        if (safe.Length > 120) safe = safe[..120];
        Console.WriteLine($"[YGuardAC] AC gate KICK {player.PlayerName} [{player.SteamID}] — {safe}");
        Server.ExecuteCommand($"kickid {player.UserId} {safe}");
    }
}
