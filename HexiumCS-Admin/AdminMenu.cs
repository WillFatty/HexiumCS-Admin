using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;

namespace HexiumCSAdmin;

internal enum AdminAction
{
    Ban,
    CallAdmin,
    Kick,
    Mute,
    Gag,
}

internal sealed record AdminPlayerEntry(ulong SteamId, int Slot, string Name);

internal sealed record AdminActionRow(AdminAction Action, string Name, string Description, string Permission);

/// <summary>
/// Multi-step Panorama admin menu (reuses hexium_calladmin layout):
/// !admin → action → player → duration (ban/mute/gag) → reason → execute.
/// Direct !ban/!kick/!mute/!gag skip the action step.
/// </summary>
internal static class AdminMenu
{
    public const string LayoutPath = "panorama/layout/custom_game/hexium_calladmin.vxml_c";
    private const string EntityName = "hexium_admin_menu";
    private const string RootPanelId = "CallAdminRoot";
    private const string OpenClass = "open";
    private const string EmptyClass = "empty";
    private const string HiddenClass = "hidden";
    private const string DisabledClass = "disabled";
    private const int RowsPerPage = 10;

    private enum Step
    {
        Actions,
        Players,
        Durations,
        Reasons,
    }

    private sealed class MenuState
    {
        public bool FromHub { get; init; }
        public AdminAction? Action { get; set; }
        public Step Step { get; set; } = Step.Players;
        public int Page { get; set; }
        public List<AdminActionRow> Actions { get; } = [];
        public List<AdminPlayerEntry> Players { get; } = [];
        public AdminPlayerEntry? Target { get; set; }
        public AdminDurationOption? Duration { get; set; }
    }

    public delegate Task ActionHandler(
        CCSPlayerController admin,
        AdminAction action,
        AdminPlayerEntry target,
        AdminDurationOption? duration,
        AdminReason reason);

    private static BasePlugin? _plugin;
    private static AdminConfig _config = new();
    private static CCSCustomHudLayout? _hud;
    private static ActionHandler? _handler;
    private static readonly Dictionary<int, MenuState> OpenSlots = new();

    public static void Initialize(BasePlugin plugin, AdminConfig config, ActionHandler handler)
    {
        _plugin = plugin;
        _config = config;
        _handler = handler;
        plugin.RegisterListener<Listeners.OnCustomHudClicked>(OnCustomHudClicked);
        plugin.RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        Server.NextFrame(() => EnsureHud());
    }

    public static void SetConfig(AdminConfig config) => _config = config;

    public static void Shutdown()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid)
                Close(player);
        }

        OpenSlots.Clear();
        if (_hud is { IsValid: true })
            _hud.Remove();
        _hud = null;

        if (_plugin is not null)
        {
            _plugin.RemoveListener<Listeners.OnCustomHudClicked>(OnCustomHudClicked);
            _plugin.RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
            _plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        }

        _plugin = null;
        _handler = null;
    }

    /// <summary>Open the !admin hub (action picker first).</summary>
    public static void OpenHub(CCSPlayerController player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        var hud = EnsureHud();
        if (hud is null)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] Panorama HUD failed to spawn. Is hexiumcs_ui mounted?");
            return;
        }

        var state = new MenuState
        {
            FromHub = true,
            Step = Step.Actions,
        };
        FillActions(player, state);
        if (state.Actions.Count == 0)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have ban/kick/mute/gag permissions.");
            return;
        }

        OpenSlots[player.Slot] = state;
        TeamChrome.Apply(hud, player, RootPanelId);
        Render(player, hud);
        hud.SetHasClassForPlayer(player, RootPanelId, OpenClass, true);
        hud.SetInputCaptureEnabled(player, true);
        Server.PrintToConsole($"[HexiumCS-Admin] Opened !admin hub for {player.PlayerName} (slot {player.Slot}).");
    }

    /// <summary>Open a direct action menu (!ban / !kick / !mute / !gag).</summary>
    public static void Open(CCSPlayerController player, AdminAction action)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        var hud = EnsureHud();
        if (hud is null)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] Panorama HUD failed to spawn. Is hexiumcs_ui mounted?");
            return;
        }

        var state = new MenuState
        {
            FromHub = false,
            Action = action,
            Step = Step.Players,
        };
        FillPlayers(player, state);

        if (state.Players.Count == 0)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] No other players online.");
            return;
        }

        OpenSlots[player.Slot] = state;
        TeamChrome.Apply(hud, player, RootPanelId);
        Render(player, hud);
        hud.SetHasClassForPlayer(player, RootPanelId, OpenClass, true);
        hud.SetInputCaptureEnabled(player, true);
    }

    public static void Close(CCSPlayerController player)
    {
        if (player is null || !player.IsValid)
            return;

        var hud = _hud;
        if (hud is { IsValid: true })
        {
            hud.SetHasClassForPlayer(player, RootPanelId, OpenClass, false);
            hud.SetInputCaptureEnabled(player, false);
        }

        OpenSlots.Remove(player.Slot);
    }

    private static void FillActions(CCSPlayerController player, MenuState state)
    {
        state.Actions.Clear();
        TryAddAction(player, state, AdminAction.Ban, "Ban", "Remove a player from all servers", "@css/ban");
        TryAddAction(player, state, AdminAction.Kick, "Kick", "Remove a player from this server", "@css/kick");
        TryAddAction(player, state, AdminAction.Mute, "Mute", "Block voice chat", "@css/chat");
        TryAddAction(player, state, AdminAction.Gag, "Gag", "Block text chat", "@css/chat");
    }

    private static void TryAddAction(
        CCSPlayerController player,
        MenuState state,
        AdminAction action,
        string name,
        string description,
        string permission)
    {
        if (!AdminManager.PlayerHasPermissions(player, permission))
            return;
        state.Actions.Add(new AdminActionRow(action, name, description, permission));
    }

    private static void FillPlayers(CCSPlayerController player, MenuState state)
    {
        state.Players.Clear();
        foreach (var other in Utilities.GetPlayers())
        {
            if (!other.IsValid || other.IsBot || other.IsHLTV)
                continue;

            // Include self so solo testing of !admin / !ban / !mute / !gag works.
            var name = other.PlayerName ?? "";
            if (other.Slot == player.Slot)
                name = $"{name} (you)";

            state.Players.Add(new AdminPlayerEntry(other.SteamID, other.Slot, name));
        }

        state.Players.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AdminDurationOption> GetDurations(MenuState state)
        => state.Action is null or AdminAction.Kick or AdminAction.CallAdmin
            ? []
            : _config.Durations;

    private static IReadOnlyList<AdminReason> GetReasons(MenuState state) => state.Action switch
    {
        AdminAction.Ban => _config.BanReasons,
        AdminAction.CallAdmin => _config.CallAdminReasons,
        AdminAction.Kick => _config.KickReasons,
        AdminAction.Mute => _config.MuteReasons,
        AdminAction.Gag => _config.GagReasons,
        _ => [],
    };

    private static string ActionLabel(AdminAction? action) => action switch
    {
        AdminAction.Ban => "Ban",
        AdminAction.CallAdmin => "Call admin",
        AdminAction.Kick => "Kick",
        AdminAction.Mute => "Mute",
        AdminAction.Gag => "Gag",
        _ => "Admin",
    };

    private static void Render(CCSPlayerController player, CCSCustomHudLayout hud)
    {
        if (!OpenSlots.TryGetValue(player.Slot, out var state))
            return;

        var durations = GetDurations(state);
        var reasons = GetReasons(state);

        var count = state.Step switch
        {
            Step.Actions => state.Actions.Count,
            Step.Players => state.Players.Count,
            Step.Durations => durations.Count,
            _ => reasons.Count,
        };
        var pageCount = Math.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
        if (state.Page >= pageCount)
            state.Page = pageCount - 1;
        if (state.Page < 0)
            state.Page = 0;

        var (title, hint) = state.Step switch
        {
            Step.Actions => ("Admin", "Select an action"),
            Step.Players => (
                ActionLabel(state.Action),
                $"Select a player to {ActionLabel(state.Action).ToLowerInvariant()}"),
            Step.Durations => (
                $"{ActionLabel(state.Action)} duration",
                state.Target is null ? "Select a duration" : $"Duration for {state.Target.Name}"),
            _ => (
                $"{ActionLabel(state.Action)} reason",
                state.Target is null ? "Select a reason" : $"Reason for {state.Target.Name}"),
        };

        hud.SetDialogVariableStringForPlayer(player, "ca_title", "ca_title", title);
        hud.SetDialogVariableStringForPlayer(player, "ca_hint", "ca_hint", hint);

        var start = state.Page * RowsPerPage;
        for (var i = 0; i < RowsPerPage; i++)
        {
            var rowId = $"ca_row{i}";
            var index = start + i;

            if (index < count)
            {
                hud.SetHasClassForPlayer(player, rowId, EmptyClass, false);
                switch (state.Step)
                {
                    case Step.Actions:
                    {
                        var entry = state.Actions[index];
                        hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}", $"ca_row{i}", entry.Name);
                        hud.SetDialogVariableStringForPlayer(
                            player, $"ca_row{i}s", $"ca_row{i}s", entry.Description);
                        break;
                    }
                    case Step.Players:
                    {
                        var entry = state.Players[index];
                        hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}", $"ca_row{i}", entry.Name);
                        hud.SetDialogVariableStringForPlayer(
                            player, $"ca_row{i}s", $"ca_row{i}s", $"SteamID: {entry.SteamId}");
                        break;
                    }
                    case Step.Durations:
                    {
                        var duration = durations[index];
                        hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}", $"ca_row{i}", duration.Name);
                        hud.SetDialogVariableStringForPlayer(
                            player, $"ca_row{i}s", $"ca_row{i}s", duration.Description);
                        break;
                    }
                    default:
                    {
                        var reason = reasons[index];
                        hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}", $"ca_row{i}", reason.Name);
                        hud.SetDialogVariableStringForPlayer(
                            player, $"ca_row{i}s", $"ca_row{i}s", reason.Description);
                        break;
                    }
                }
            }
            else
            {
                hud.SetHasClassForPlayer(player, rowId, EmptyClass, true);
                hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}", $"ca_row{i}", string.Empty);
                hud.SetDialogVariableStringForPlayer(player, $"ca_row{i}s", $"ca_row{i}s", string.Empty);
            }
        }

        var hasPrev = state.Page > 0;
        var hasNext = state.Page < pageCount - 1;
        var showBack = state.Step != Step.Actions && (state.FromHub || state.Step != Step.Players);
        hud.SetHasClassForPlayer(player, "ca_prev", DisabledClass, !hasPrev);
        hud.SetHasClassForPlayer(player, "ca_next", DisabledClass, !hasNext);
        hud.SetHasClassForPlayer(player, "ca_back", HiddenClass, !showBack);
        hud.SetDialogVariableStringForPlayer(
            player,
            "ca_page",
            "ca_page",
            $"Page {state.Page + 1} / {pageCount}");
    }

    private static void OnCustomHudClicked(CCSPlayerController player, CCSCustomHudLayout layout, string buttonId)
    {
        if (!IsOurHud(layout) || player is null || !player.IsValid)
            return;

        if (!OpenSlots.TryGetValue(player.Slot, out var state) || _hud is null || !_hud.IsValid)
            return;

        switch (buttonId)
        {
            case "ca_close":
                Close(player);
                return;
            case "ca_prev":
                if (state.Page <= 0)
                    return;
                state.Page--;
                Render(player, _hud);
                return;
            case "ca_next":
            {
                var count = GetStepCount(state);
                var pageCount = Math.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
                if (state.Page >= pageCount - 1)
                    return;
                state.Page++;
                Render(player, _hud);
                return;
            }
            case "ca_back":
                GoBack(player, state);
                Render(player, _hud);
                return;
        }

        if (!buttonId.StartsWith("ca_row", StringComparison.Ordinal) ||
            !int.TryParse(buttonId.AsSpan(6), out var row) ||
            row < 0 || row >= RowsPerPage)
            return;

        var count2 = GetStepCount(state);
        var index = state.Page * RowsPerPage + row;
        if (index < 0 || index >= count2)
            return;

        switch (state.Step)
        {
            case Step.Actions:
            {
                var selected = state.Actions[index];
                if (!AdminManager.PlayerHasPermissions(player, selected.Permission))
                {
                    player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
                    return;
                }

                if (selected.Action != AdminAction.Kick &&
                    (string.IsNullOrWhiteSpace(_config.ApiBaseUrl) || string.IsNullOrWhiteSpace(_config.ApiKey)))
                {
                    player.PrintToChat(
                        $"{ChatColors.Red}[Admin] InventorySim is not configured (ApiBaseUrl / ApiKey).");
                    return;
                }

                FillPlayers(player, state);
                if (state.Players.Count == 0)
                {
                    player.PrintToChat($"{ChatColors.Red}[Admin] No other players online.");
                    return;
                }

                state.Action = selected.Action;
                state.Step = Step.Players;
                state.Page = 0;
                state.Target = null;
                state.Duration = null;
                Render(player, _hud);
                return;
            }

            case Step.Players:
                state.Target = state.Players[index];
                state.Step = state.Action is AdminAction.Kick or AdminAction.CallAdmin
                    ? Step.Reasons
                    : Step.Durations;
                state.Page = 0;
                Render(player, _hud);
                return;

            case Step.Durations:
                state.Duration = GetDurations(state)[index];
                state.Step = Step.Reasons;
                state.Page = 0;
                Render(player, _hud);
                return;

            case Step.Reasons:
            {
                var reason = GetReasons(state)[index];
                var target = state.Target;
                var duration = state.Duration;
                var action = state.Action;
                var handler = _handler;
                Close(player);

                if (target is null || action is null || handler is null)
                    return;

                _ = InvokeHandlerAsync(handler, player, action.Value, target, duration, reason);
                return;
            }
        }
    }

    private static int GetStepCount(MenuState state) => state.Step switch
    {
        Step.Actions => state.Actions.Count,
        Step.Players => state.Players.Count,
        Step.Durations => GetDurations(state).Count,
        _ => GetReasons(state).Count,
    };

    private static void GoBack(CCSPlayerController player, MenuState state)
    {
        state.Page = 0;
        switch (state.Step)
        {
            case Step.Reasons:
                if (state.Action is AdminAction.Kick or AdminAction.CallAdmin)
                {
                    state.Step = Step.Players;
                    state.Target = null;
                }
                else
                {
                    state.Step = Step.Durations;
                    state.Duration = null;
                }
                break;
            case Step.Durations:
                state.Step = Step.Players;
                state.Target = null;
                break;
            case Step.Players:
                if (!state.FromHub)
                    return;
                state.Action = null;
                state.Target = null;
                state.Duration = null;
                FillActions(player, state);
                state.Step = Step.Actions;
                break;
        }
    }

    private static async Task InvokeHandlerAsync(
        ActionHandler handler,
        CCSPlayerController admin,
        AdminAction action,
        AdminPlayerEntry target,
        AdminDurationOption? duration,
        AdminReason reason)
    {
        try
        {
            await handler(admin, action, target, duration, reason);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[HexiumCS-Admin] Action handler failed: {ex}");
        }
    }

    private static void OnClientDisconnect(int playerSlot)
    {
        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player is not null)
            Close(player);
        else
            OpenSlots.Remove(playerSlot);
    }

    private static void OnMapStart(string mapName)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid)
                Close(player);
        }

        OpenSlots.Clear();
        _hud = null;
        Server.NextFrame(() => EnsureHud());
    }

    private static void OnMapEnd()
    {
        OpenSlots.Clear();
        _hud = null;
    }

    private static bool IsOurHud(CCSCustomHudLayout layout)
    {
        if (_hud is null || !_hud.IsValid)
            return false;
        return layout.Handle == _hud.Handle ||
               (layout.Entity?.Name is string name && name == EntityName);
    }

    private static CCSCustomHudLayout? EnsureHud()
    {
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
            Server.PrintToConsole("[HexiumCS-Admin] Failed to create custom_hud_layout entity.");
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
