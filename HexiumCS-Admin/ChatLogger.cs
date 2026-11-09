using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace HexiumCSAdmin;

/// <summary>
/// Buffers player chat (say / say_team) and ships it to InventorySim in batches,
/// tagged with the current match ID so logs correlate with call-admin tickets.
/// </summary>
internal static class ChatLogger
{
    private const int FlushIntervalSeconds = 10;
    private const int MaxQueueSize = 2000;
    private const int MaxTextLength = 500;

    private sealed record ChatEntry(
        string SteamId,
        string PlayerName,
        string Text,
        bool TeamOnly);

    private static readonly Queue<ChatEntry> Pending = new();
    private static BasePlugin? _plugin;
    private static AdminConfig _config = new();
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _flushTimer;
    private static string _matchId = "";
    private static string _mapName = "";

    /// <summary>Match ID shared by chat logs and call-admin tickets on this server.</summary>
    public static string CurrentMatchId => EnsureMatch();

    public static void Initialize(BasePlugin plugin, AdminConfig config)
    {
        _plugin = plugin;
        _config = config;
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.AddCommandListener("say", OnSay);
        plugin.AddCommandListener("say_team", OnSayTeam);
        EnsureMatch();
        ScheduleFlush();
    }

    public static void SetConfig(AdminConfig config) => _config = config;

    public static void Shutdown()
    {
        _flushTimer?.Kill();
        _flushTimer = null;
        Pending.Clear();
        _matchId = "";
        _mapName = "";

        if (_plugin is not null)
        {
            _plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
            _plugin.RemoveCommandListener("say", OnSay, HookMode.Pre);
            _plugin.RemoveCommandListener("say_team", OnSayTeam, HookMode.Pre);
        }

        _plugin = null;
    }

    private static HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        Enqueue(player, command.ArgString, teamOnly: false);
        return HookResult.Continue;
    }

    private static HookResult OnSayTeam(CCSPlayerController? player, CommandInfo command)
    {
        Enqueue(player, command.ArgString, teamOnly: true);
        return HookResult.Continue;
    }

    private static void Enqueue(CCSPlayerController? player, string? text, bool teamOnly)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var trimmed = text.Trim();
        if (trimmed.Length > MaxTextLength)
            trimmed = trimmed[..MaxTextLength];

        if (Pending.Count >= MaxQueueSize)
            Pending.Dequeue();

        Pending.Enqueue(new ChatEntry(
            player.SteamID.ToString(),
            player.PlayerName ?? "",
            trimmed,
            teamOnly));
    }

    private static void ScheduleFlush()
    {
        if (_plugin is null)
            return;

        _flushTimer?.Kill();
        _flushTimer = _plugin.AddTimer(FlushIntervalSeconds, () =>
        {
            _flushTimer = null;
            _ = FlushAsync();
            ScheduleFlush();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Generates a match ID for the current map if none exists yet — covers
    /// plugin loads that happen mid-map (hot reload, late install).
    /// </summary>
    private static string EnsureMatch()
    {
        if (_matchId.Length > 0)
            return _matchId;

        var map = Server.MapName;
        if (string.IsNullOrWhiteSpace(map))
            return "";

        _mapName = map;
        _matchId =
            $"{Sanitize(_config.ServerName)}-{map}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..6]}";
        Server.PrintToConsole($"[HexiumCS-Admin] Chat logging match {_matchId} on {map}.");
        return _matchId;
    }

    private static async Task FlushAsync()
    {
        var matchId = EnsureMatch();
        if (Pending.Count == 0 ||
            string.IsNullOrWhiteSpace(_config.ApiBaseUrl) ||
            string.IsNullOrWhiteSpace(_config.ApiKey) ||
            matchId.Length == 0)
            return;

        var batch = new List<ChatEntry>(Pending.Count);
        while (Pending.Count > 0)
            batch.Add(Pending.Dequeue());

        var ok = await InventorySimClient.SendChatLogsAsync(
            _config.ApiBaseUrl,
            _config.ApiKey,
            _config.ServerName,
            matchId,
            _mapName,
            batch.Select(entry => new InventorySimClient.ChatLogItem(
                entry.SteamId,
                entry.PlayerName,
                entry.Text,
                entry.TeamOnly)).ToList());

        // The await above resumes on a thread-pool thread — hop back to the
        // main thread before touching natives or the shared queue.
        Server.NextFrame(() =>
        {
            if (ok)
            {
                Server.PrintToConsole(
                    $"[HexiumCS-Admin] Sent {batch.Count} chat message(s) for match {matchId}.");
                return;
            }

            Server.PrintToConsole(
                $"[HexiumCS-Admin] Chat log flush failed ({batch.Count} messages re-queued).");
            foreach (var entry in batch)
            {
                if (Pending.Count >= MaxQueueSize)
                    break;
                Pending.Enqueue(entry);
            }
        });
    }

    private static void OnMapStart(string mapName)
    {
        _ = FlushAsync();
        _mapName = mapName;
        _matchId =
            $"{Sanitize(_config.ServerName)}-{mapName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..6]}";
        ScheduleFlush();
    }

    private static void OnMapEnd()
    {
        _ = FlushAsync();
        _flushTimer?.Kill();
        _flushTimer = null;
    }

    private static string Sanitize(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray();
        var result = new string(chars).Trim('-');
        return result.Length == 0 ? "server" : result[..Math.Min(result.Length, 32)];
    }
}
