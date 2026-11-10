using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace HexiumCSAdmin;

/// <summary>
/// Keeps a local cache of active bans/mutes/gags from InventorySim and enforces them.
/// Shared across every server running this plugin (or CallAdmin for bans).
/// </summary>
internal static class AdminPenalties
{
    private const int MinSyncIntervalSeconds = 2;

    private sealed record CachedPenalty(DateTime? ExpiresAt, string Reason);

    private static BasePlugin? _plugin;
    private static AdminConfig _config = new();
    private static readonly Dictionary<ulong, CachedPenalty> Bans = new();
    private static readonly Dictionary<ulong, CachedPenalty> Mutes = new();
    private static readonly Dictionary<ulong, CachedPenalty> Gags = new();
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _syncTimer;

    public static void Initialize(BasePlugin plugin, AdminConfig config)
    {
        _plugin = plugin;
        _config = config;
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.AddCommandListener("say", OnSay);
        plugin.AddCommandListener("say_team", OnSay);
        SyncAndSchedule();
    }

    public static void SetConfig(AdminConfig config) => _config = config;

    public static void Shutdown()
    {
        _syncTimer?.Kill();
        _syncTimer = null;
        Bans.Clear();
        Mutes.Clear();
        Gags.Clear();

        if (_plugin is not null)
        {
            _plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
            _plugin.RemoveCommandListener("say", OnSay, HookMode.Pre);
            _plugin.RemoveCommandListener("say_team", OnSay, HookMode.Pre);
        }

        _plugin = null;
    }

    public static void CheckPlayer(CCSPlayerController player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (TryGet(Bans, player.SteamID, out var ban))
        {
            var reason = FormatExpiry(ban, "Banned");
            Server.PrintToConsole(
                $"[HexiumCS-Admin] {player.PlayerName} ({player.SteamID}) is banned — kicking. {reason}");
            Kick(player, reason);
            return;
        }

        ApplyVoiceMute(player, TryGet(Mutes, player.SteamID, out _));
    }

    public static bool IsGagged(ulong steamId) => TryGet(Gags, steamId, out _);

    public static bool IsMuted(ulong steamId) => TryGet(Mutes, steamId, out _);

    public static void Kick(CCSPlayerController player, string reason)
    {
        var cleanReason = reason.Replace('"', '\'').Replace('\n', ' ').Replace('\r', ' ');
        Server.ExecuteCommand($"kickid {player.UserId} \"{cleanReason}\"");
    }

    /// <summary>Optimistic local cache update after a successful API create (before next sync).</summary>
    public static void CacheLocal(
        string type,
        ulong steamId,
        string reason,
        string duration)
    {
        var expiresAt = ParseDuration(duration);
        var cached = new CachedPenalty(expiresAt, reason);
        switch (type)
        {
            case "ban":
                Bans[steamId] = cached;
                break;
            case "mute":
                Mutes[steamId] = cached;
                break;
            case "gag":
                Gags[steamId] = cached;
                break;
        }
    }

    private static DateTime? ParseDuration(string duration) => duration switch
    {
        "1h" => DateTime.UtcNow.AddHours(1),
        "6h" => DateTime.UtcNow.AddHours(6),
        "24h" => DateTime.UtcNow.AddDays(1),
        "3d" => DateTime.UtcNow.AddDays(3),
        "7d" => DateTime.UtcNow.AddDays(7),
        "permanent" => null,
        _ => DateTime.UtcNow.AddHours(1),
    };

    private static HookResult OnSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        if (!TryGet(Gags, player.SteamID, out var gag))
            return HookResult.Continue;

        player.PrintToChat($"{ChatColors.Red}[Admin] You are gagged: {FormatExpiry(gag, "Gag")}");
        return HookResult.Handled;
    }

    private static void ApplyVoiceMute(CCSPlayerController player, bool muted)
    {
        try
        {
            player.VoiceFlags = muted ? VoiceFlags.Muted : VoiceFlags.Normal;
        }
        catch
        {
            // Ignore if voice flags are unavailable on this build.
        }
    }

    private static void EnforceOnlinePlayers()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            if (TryGet(Bans, player.SteamID, out var ban))
            {
                Kick(player, FormatExpiry(ban, "Banned"));
                continue;
            }

            ApplyVoiceMute(player, TryGet(Mutes, player.SteamID, out _));
        }
    }

    private static bool TryGet(Dictionary<ulong, CachedPenalty> map, ulong steamId, out CachedPenalty penalty)
    {
        if (map.TryGetValue(steamId, out var cached) &&
            (cached.ExpiresAt is null || cached.ExpiresAt > DateTime.UtcNow))
        {
            penalty = cached;
            return true;
        }

        penalty = default!;
        return false;
    }

    private static string FormatExpiry(CachedPenalty penalty, string label)
    {
        var expiry = penalty.ExpiresAt is { } expires
            ? $"{label} until {expires:yyyy-MM-dd HH:mm} UTC"
            : $"{label} permanently";
        return string.IsNullOrEmpty(penalty.Reason) ? expiry : $"{penalty.Reason} — {expiry}";
    }

    private static void SyncAndSchedule()
    {
        _ = SyncAsync();
        ScheduleNext();
    }

    private static void ScheduleNext()
    {
        if (_plugin is null)
            return;

        var interval = Math.Max(MinSyncIntervalSeconds, _config.PenaltySyncIntervalSeconds);
        _syncTimer?.Kill();
        _syncTimer = _plugin.AddTimer(interval, () =>
        {
            _syncTimer = null;
            SyncAndSchedule();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static async Task SyncAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiBaseUrl) || string.IsNullOrWhiteSpace(_config.ApiKey))
            return;

        var snapshot = await InventorySimClient.GetPenaltiesAsync(_config.ApiBaseUrl, _config.ApiKey);
        if (snapshot is null)
        {
            Server.PrintToConsole("[HexiumCS-Admin] Penalty sync failed — keeping previous cache.");
            return;
        }

        Server.NextFrame(() =>
        {
            ReplaceCache(Bans, snapshot.Bans);
            ReplaceCache(Mutes, snapshot.Mutes);
            ReplaceCache(Gags, snapshot.Gags);
            EnforceOnlinePlayers();
        });
    }

    private static void ReplaceCache(
        Dictionary<ulong, CachedPenalty> target,
        IReadOnlyList<InventorySimClient.RemotePenalty> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            if (!ulong.TryParse(item.SteamId, out var steamId))
                continue;

            DateTime? expiresAt = null;
            if (item.ExpiresAt is not null &&
                DateTime.TryParse(
                    item.ExpiresAt,
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed))
                expiresAt = parsed;

            target[steamId] = new CachedPenalty(expiresAt, item.Reason);
        }
    }

    private static void OnMapStart(string mapName)
    {
        Bans.Clear();
        Mutes.Clear();
        Gags.Clear();
        _syncTimer?.Kill();
        _syncTimer = null;
        Server.NextFrame(() => SyncAndSchedule());
    }

    private static void OnMapEnd()
    {
        Bans.Clear();
        Mutes.Clear();
        Gags.Clear();
        _syncTimer?.Kill();
        _syncTimer = null;
    }
}
