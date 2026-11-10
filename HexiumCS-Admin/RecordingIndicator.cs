using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace HexiumCSAdmin;

/// <summary>
/// Red-dot HUD indicator shown to players while DemoRecorder is capturing a
/// tv_record demo — but only while a player has the scoreboard (TAB) open.
/// Driven by <see cref="DemoRecorder"/> via <see cref="SetRecording"/>; the
/// panorama layout lives in the hexiumcs_ui addon (hexium_recording).
/// </summary>
internal static class RecordingIndicator
{
    public const string LayoutPath = "panorama/layout/custom_game/hexium_recording.vxml_c";
    private const string EntityName = "hexium_admin_recording";
    private const string RootPanelId = "RecordingRoot";
    private const string ShowClass = "show";
    private const float PollIntervalSeconds = 0.15f;

    private static BasePlugin? _plugin;
    private static CCSCustomHudLayout? _hud;
    private static CounterStrikeSharp.API.Modules.Timers.Timer? _pollTimer;
    private static bool _recording;
    private static readonly HashSet<int> ShownSlots = new();

    public static void Initialize(BasePlugin plugin)
    {
        _plugin = plugin;
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        Server.NextFrame(() => EnsureHud());
        SchedulePoll();
    }

    public static void Shutdown()
    {
        _pollTimer?.Kill();
        _pollTimer = null;

        if (_plugin is not null)
        {
            _plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        }

        if (_hud is { IsValid: true })
            _hud.Remove();
        _hud = null;
        _plugin = null;
        _recording = false;
        ShownSlots.Clear();
    }

    /// <summary>Show or hide the recording dot (while TAB is held) for all players.</summary>
    public static void SetRecording(bool recording) => _recording = recording;

    /// <summary>Debug helper for !recdebug — dumps the raw button state slots.</summary>
    public static string DescribeButtons(CCSPlayerController player)
    {
        try
        {
            var movementServices = player.Pawn?.Value?.MovementServices;
            if (movementServices is null)
                return "no movement services";

            var states = movementServices.Buttons.ButtonStates;
            var parts = new List<string>();
            for (var i = 0; i < states.Length; i++)
                parts.Add($"{i}:{states[i]:x16}");
            return string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private static void SchedulePoll()
    {
        if (_plugin is null)
            return;

        _pollTimer?.Kill();
        _pollTimer = _plugin.AddTimer(PollIntervalSeconds, Poll,
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void Poll()
    {
        if (_plugin is null)
            return;

        SchedulePoll();

        // Always resolve the HUD — it is also needed to clear the dot when
        // recording stops or a player releases TAB.
        var hud = EnsureHud();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot || player.IsHLTV)
                continue;

            var show = false;
            if (_recording)
            {
                try
                {
                    // ButtonStates is [down, changed, scroll]; the scoreboard
                    // bit may not land in index 0, so scan all slots.
                    var movementServices = player.Pawn?.Value?.MovementServices;
                    if (movementServices is not null)
                    {
                        foreach (var state in movementServices.Buttons.ButtonStates)
                        {
                            if ((state & (ulong)PlayerButtons.Scoreboard) != 0)
                            {
                                show = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    show = false;
                }
            }

            var slot = player.Slot;
            if (ShownSlots.Contains(slot) == show)
                continue;

            try
            {
                hud?.SetHasClassForPlayer(player, RootPanelId, ShowClass, show);
            }
            catch
            {
                // Never let the indicator break gameplay.
            }

            if (show)
                ShownSlots.Add(slot);
            else
                ShownSlots.Remove(slot);
        }

        // Drop stale slots so a rejoin doesn't inherit a stale "shown" state.
        ShownSlots.RemoveWhere(slot =>
            Utilities.GetPlayerFromSlot(slot) is not { IsValid: true });
    }

    private static void OnMapStart(string mapName)
    {
        _hud = null;
        ShownSlots.Clear();
        Server.NextFrame(() => EnsureHud());
        SchedulePoll();
    }

    private static void OnMapEnd()
    {
        _hud = null;
        ShownSlots.Clear();
    }

    private static CCSCustomHudLayout? EnsureHud()
    {
        if (_plugin is null)
            return null;

        if (_hud is { IsValid: true })
            return _hud;

        var existing = Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout")
            .FirstOrDefault(e => e.IsValid && string.Equals(e.Entity?.Name, EntityName, StringComparison.Ordinal));

        if (existing is not null)
        {
            _hud = existing;
            return _hud;
        }

        var hud = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
        if (hud is null || !hud.IsValid)
        {
            Server.PrintToConsole("[HexiumCS-Admin] Failed to create recording HUD entity.");
            return null;
        }

        if (hud.Entity is not null)
            hud.Entity.Name = EntityName;

        hud.StrLayout = LayoutPath;
        hud.DispatchSpawn();
        _hud = hud;
        Server.PrintToConsole($"[HexiumCS-Admin] Spawned HUD layout '{LayoutPath}'.");
        return _hud;
    }
}
