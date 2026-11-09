using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexiumCSAdmin;

internal static class InventorySimClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record CreateResult(bool Success, string? Id, string? Error);

    public sealed record RemotePenalty(
        [property: JsonPropertyName("steamId")] string SteamId,
        [property: JsonPropertyName("playerName")] string PlayerName,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("expiresAt")] string? ExpiresAt);

    public sealed record PenaltiesSnapshot(
        IReadOnlyList<RemotePenalty> Bans,
        IReadOnlyList<RemotePenalty> Mutes,
        IReadOnlyList<RemotePenalty> Gags);

    public static async Task<CreateResult> CreatePenaltyAsync(
        string apiBaseUrl,
        string apiKey,
        string type,
        ulong adminSteamId,
        string adminName,
        ulong targetSteamId,
        string targetName,
        string reason,
        string duration,
        string serverName = "")
    {
        try
        {
            var url = apiBaseUrl.TrimEnd('/') + "/api/admin/penalties";
            using var response = await Http.PostAsJsonAsync(url, new
            {
                apiKey,
                type,
                adminSteamId = adminSteamId.ToString(),
                adminName,
                targetSteamId = targetSteamId.ToString(),
                targetName,
                reason,
                duration,
                serverName,
            });

            if (!response.IsSuccessStatusCode)
                return new CreateResult(false, null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            return new CreateResult(true, id, null);
        }
        catch (Exception ex)
        {
            return new CreateResult(false, null, ex.Message);
        }
    }

    public static async Task<CreateResult> CreateCallAdminTicketAsync(
        string apiBaseUrl,
        string apiKey,
        ulong reporterSteamId,
        string reporterName,
        ulong targetSteamId,
        string targetName,
        string reason,
        string matchId = "",
        string serverName = "")
    {
        try
        {
            var url = apiBaseUrl.TrimEnd('/') + "/api/calladmin/ticket";
            using var response = await Http.PostAsJsonAsync(url, new
            {
                apiKey,
                reporterSteamId = reporterSteamId.ToString(),
                reporterName,
                targetSteamId = targetSteamId.ToString(),
                targetName,
                reason,
                matchId,
                serverName,
            });

            if (!response.IsSuccessStatusCode)
                return new CreateResult(false, null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            return new CreateResult(true, id, null);
        }
        catch (Exception ex)
        {
            return new CreateResult(false, null, ex.Message);
        }
    }

    public sealed record ChatLogItem(
        string SteamId,
        string PlayerName,
        string Text,
        bool TeamOnly);

    public static async Task<bool> SendChatLogsAsync(
        string apiBaseUrl,
        string apiKey,
        string serverName,
        string matchId,
        string map,
        IReadOnlyList<ChatLogItem> messages)
    {
        try
        {
            var url = apiBaseUrl.TrimEnd('/') + "/api/admin/chat-logs";
            using var response = await Http.PostAsJsonAsync(url, new
            {
                apiKey,
                serverName,
                matchId,
                map,
                messages = messages.Select(m => new
                {
                    steamId = m.SteamId,
                    playerName = m.PlayerName,
                    text = m.Text,
                    teamOnly = m.TeamOnly,
                }),
            });
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns null on failure so the local cache is kept.</summary>
    public static async Task<PenaltiesSnapshot?> GetPenaltiesAsync(string apiBaseUrl, string apiKey)
    {
        try
        {
            var url =
                $"{apiBaseUrl.TrimEnd('/')}/api/admin/penalties?apiKey={Uri.EscapeDataString(apiKey)}";
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            return new PenaltiesSnapshot(
                ParseList(doc.RootElement, "bans"),
                ParseList(doc.RootElement, "mutes"),
                ParseList(doc.RootElement, "gags"));
        }
        catch
        {
            return null;
        }
    }

    private static List<RemotePenalty> ParseList(JsonElement root, string property)
    {
        var list = new List<RemotePenalty>();
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var element in arr.EnumerateArray())
        {
            var steamId = element.TryGetProperty("steamId", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(steamId) || !ulong.TryParse(steamId, out _))
                continue;

            list.Add(new RemotePenalty(
                steamId,
                element.TryGetProperty("playerName", out var n) ? n.GetString() ?? "" : "",
                element.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                element.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null));
        }

        return list;
    }
}
