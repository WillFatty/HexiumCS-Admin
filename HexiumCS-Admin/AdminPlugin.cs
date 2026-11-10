using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using HexiumCSUI;

namespace HexiumCSAdmin;

/// <summary>
/// Panorama admin tools: !ban !kick !mute !gag.
/// Penalties sync through InventorySim so they apply on every server.
/// </summary>
public class AdminPlugin : BasePlugin, IPluginConfig<AdminConfig>
{
    public override string ModuleName => "HexiumCS-Admin";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "Hexium";
    public override string ModuleDescription =>
        "Panorama ban/kick/mute/gag menus with InventorySim multi-server sync.";

    public AdminConfig Config { get; set; } = new();

    public void OnConfigParsed(AdminConfig config)
    {
        config.Durations ??= [];
        config.CallAdminReasons ??= [];
        config.BanReasons ??= [];
        config.KickReasons ??= [];
        config.MuteReasons ??= [];
        config.GagReasons ??= [];
        Config = config;
        AdminMenu.SetConfig(config);
        AdminPenalties.SetConfig(config);
        ChatLogger.SetConfig(config);
        DemoRecorder.SetConfig(config);
    }

    public override void Load(bool hotReload)
    {
        AdminMenu.Initialize(this, Config, OnActionRequestedAsync);
        AdminPenalties.Initialize(this, Config);
        ChatLogger.Initialize(this, Config);
        DemoRecorder.Initialize(this, Config);
    }

    public override void Unload(bool hotReload)
    {
        DemoRecorder.Shutdown();
        ChatLogger.Shutdown();
        AdminPenalties.Shutdown();
        AdminMenu.Shutdown();
    }

    [ConsoleCommand("css_admin", "Open the admin menu (!admin)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnAdminCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpenHub(player);

    [ConsoleCommand("css_calladmin", "Open the call-admin menu (!calladmin)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCallAdminCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpenCallAdmin(player);

    [ConsoleCommand("css_ban", "Open the ban menu (!ban)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/ban")]
    public void OnBanCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpen(player, AdminAction.Ban, "@css/ban");

    [ConsoleCommand("css_kick", "Open the kick menu (!kick)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/kick")]
    public void OnKickCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpen(player, AdminAction.Kick, "@css/kick");

    [ConsoleCommand("css_mute", "Open the mute menu (!mute)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/chat")]
    public void OnMuteCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpen(player, AdminAction.Mute, "@css/chat");

    [ConsoleCommand("css_gag", "Open the gag menu (!gag)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/chat")]
    public void OnGagCommand(CCSPlayerController? player, CommandInfo command)
        => TryOpen(player, AdminAction.Gag, "@css/chat");

    [ConsoleCommand("css_record", "Toggle match demo recording (!record)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRecordCommand(CCSPlayerController? player, CommandInfo command)
        => TryToggleRecord(player);

    [ConsoleCommand("css_stoprecording", "Stop match demo recording (!stoprecording)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnStopRecordingCommand(CCSPlayerController? player, CommandInfo command)
        => TryStopRecord(player);

    [ConsoleCommand("css_recdebug", "Print raw button states (!recdebug)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnRecDebugCommand(CCSPlayerController? player, CommandInfo command)
        => TryRecDebug(player);

    [GameEventHandler]
    public HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayerFromSlot(@event.Userid);
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var message = @event.Text?.Trim();
        if (string.IsNullOrEmpty(message))
            return HookResult.Continue;

        var text = message.ToLowerInvariant();
        if (text is "!admin" or "/admin")
        {
            TryOpenHub(player);
            return HookResult.Handled;
        }

        if (text is "!calladmin" or "/calladmin")
        {
            TryOpenCallAdmin(player);
            return HookResult.Handled;
        }

        // !record / !stop / !recdebug are handled by the css_* console commands
        // below — CSS auto-dispatches !chat to css_ commands, so intercepting
        // them here as well would run the handler twice (toggle = start+stop).

        AdminAction? action = text switch
        {
            "!ban" or "/ban" => AdminAction.Ban,
            "!kick" or "/kick" => AdminAction.Kick,
            "!mute" or "/mute" => AdminAction.Mute,
            "!gag" or "/gag" => AdminAction.Gag,
            _ => null,
        };

        if (action is null)
            return HookResult.Continue;

        var permission = action switch
        {
            AdminAction.Ban => "@css/ban",
            AdminAction.Kick => "@css/kick",
            _ => "@css/chat",
        };

        if (!AdminManager.PlayerHasPermissions(player, permission))
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return HookResult.Handled;
        }

        TryOpen(player, action.Value, permission);
        return HookResult.Handled;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null)
            AdminPenalties.CheckPlayer(player);
        return HookResult.Continue;
    }

    private void TryOpenHub(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        var canAny =
            AdminManager.PlayerHasPermissions(player, "@css/ban") ||
            AdminManager.PlayerHasPermissions(player, "@css/kick") ||
            AdminManager.PlayerHasPermissions(player, "@css/chat");

        if (!canAny)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return;
        }

        AdminMenu.OpenHub(player);
    }

    private void TryOpenCallAdmin(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (string.IsNullOrWhiteSpace(Config.ApiBaseUrl) || string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            player.PrintToChat(
                $"{ChatColors.Red}[Admin] InventorySim is not configured (ApiBaseUrl / ApiKey).");
            return;
        }

        if (Config.CallAdminReasons.Count == 0)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] No call-admin reasons configured.");
            return;
        }

        AdminMenu.Open(player, AdminAction.CallAdmin);
    }

    /// <summary>
    /// Sends command feedback as a HexiumCS-UI panorama notification when the
    /// UI plugin is loaded, falling back to chat otherwise. Message may use
    /// {color} tags (e.g. {green}, {red}).
    /// </summary>
    internal static void NotifyResult(CCSPlayerController player, string message)
    {
        if (HexiumUI.IsInitialized)
        {
            HexiumUI.ShowNotification(player, "Admin", message, 5);
            return;
        }

        player.PrintToChat($"{ChatColors.Grey}[Admin]{ChatColors.Default} {message}");
    }

    private void TryRecDebug(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (!AdminManager.PlayerHasPermissions(player, "@css/generic"))
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return;
        }

        NotifyResult(player, $"Buttons: {RecordingIndicator.DescribeButtons(player)}");
    }

    private void TryToggleRecord(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (!AdminManager.PlayerHasPermissions(player, "@css/generic"))
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return;
        }

        var wasRecording = DemoRecorder.IsRecording;
        var state = DemoRecorder.ToggleRecording();
        if (state is null)
        {
            NotifyResult(player, "Demo recording is not enabled on this server.");
            return;
        }

        if (state.Value)
        {
            NotifyResult(player, "Recording match demo.");
        }
        else if (wasRecording)
        {
            NotifyResult(player, "Recording stopped.");
        }
        else
        {
            NotifyResult(player, "Failed to start recording (tv_enable may be false; check server console).");
        }
    }

    private void TryStopRecord(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (!AdminManager.PlayerHasPermissions(player, "@css/generic"))
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return;
        }

        if (DemoRecorder.StopRecording())
        {
            NotifyResult(player, "Recording stopped.");
        }
        else
        {
            NotifyResult(player, "No recording in progress.");
        }
    }

    private void TryOpen(CCSPlayerController? player, AdminAction action, string permission)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        if (!AdminManager.PlayerHasPermissions(player, permission))
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] You do not have permission for that.");
            return;
        }

        if (action != AdminAction.Kick &&
            (string.IsNullOrWhiteSpace(Config.ApiBaseUrl) || string.IsNullOrWhiteSpace(Config.ApiKey)))
        {
            player.PrintToChat(
                $"{ChatColors.Red}[Admin] InventorySim is not configured (ApiBaseUrl / ApiKey).");
            return;
        }

        if (action != AdminAction.Kick && Config.Durations.Count == 0)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] No durations configured.");
            return;
        }

        var reasons = action switch
        {
            AdminAction.Ban => Config.BanReasons,
            AdminAction.Kick => Config.KickReasons,
            AdminAction.Mute => Config.MuteReasons,
            AdminAction.Gag => Config.GagReasons,
            _ => [],
        };

        if (reasons.Count == 0)
        {
            player.PrintToChat($"{ChatColors.Red}[Admin] No reasons configured for {action}.");
            return;
        }

        AdminMenu.Open(player, action);
    }

    private async Task OnActionRequestedAsync(
        CCSPlayerController admin,
        AdminAction action,
        AdminPlayerEntry target,
        AdminDurationOption? duration,
        AdminReason reason)
    {
        var adminSlot = admin.Slot;
        var adminSteamId = admin.SteamID;
        var adminName = admin.PlayerName ?? "";

        if (action == AdminAction.CallAdmin)
        {
            var ticketResult = await InventorySimClient.CreateCallAdminTicketAsync(
                Config.ApiBaseUrl,
                Config.ApiKey,
                adminSteamId,
                adminName,
                target.SteamId,
                target.Name,
                reason.Name,
                ChatLogger.CurrentMatchId,
                Config.ServerName);

            Server.NextFrame(() =>
            {
                var liveAdmin = Utilities.GetPlayerFromSlot(adminSlot);
                if (!ticketResult.Success)
                {
                    liveAdmin?.PrintToChat(
                        $"{ChatColors.Red}[Admin] Failed to send call-admin request ({ticketResult.Error}).");
                    Server.PrintToConsole(
                        $"[HexiumCS-Admin] Call-admin ticket failed for {target.Name}: {ticketResult.Error}");
                    return;
                }

                liveAdmin?.PrintToChat(
                    $"{ChatColors.Green}[Admin] Call-admin request sent. Staff will respond soon.");
            });
            return;
        }

        if (action == AdminAction.Kick)
        {
            Server.NextFrame(() =>
            {
                var liveAdmin = Utilities.GetPlayerFromSlot(adminSlot);
                var liveTarget = Utilities.GetPlayerFromSlot(target.Slot);
                if (liveTarget is null || !liveTarget.IsValid || liveTarget.SteamID != target.SteamId)
                {
                    liveAdmin?.PrintToChat($"{ChatColors.Red}[Admin] Target is no longer online.");
                    return;
                }

                var kickReason = string.IsNullOrWhiteSpace(reason.Name) ? "Kicked by admin" : reason.Name;
                AdminPenalties.Kick(liveTarget, kickReason);
                Broadcast($"{ChatColors.LightRed}{liveTarget.PlayerName}{ChatColors.Default} was kicked ({kickReason}).");
                liveAdmin?.PrintToChat($"{ChatColors.Green}[Admin] Kicked {liveTarget.PlayerName}.");
            });
            return;
        }

        var type = action switch
        {
            AdminAction.Ban => "ban",
            AdminAction.Mute => "mute",
            AdminAction.Gag => "gag",
            _ => "ban",
        };
        var durationValue = duration?.Value ?? "1h";

        var result = await InventorySimClient.CreatePenaltyAsync(
            Config.ApiBaseUrl,
            Config.ApiKey,
            type,
            adminSteamId,
            adminName,
            target.SteamId,
            target.Name,
            reason.Name,
            durationValue,
            Config.ServerName);

        Server.NextFrame(() =>
        {
            var liveAdmin = Utilities.GetPlayerFromSlot(adminSlot);
            if (!result.Success)
            {
                liveAdmin?.PrintToChat(
                    $"{ChatColors.Red}[Admin] Failed to {type} ({result.Error}).");
                Server.PrintToConsole(
                    $"[HexiumCS-Admin] Create {type} failed for {target.Name}: {result.Error}");
                return;
            }

            AdminPenalties.CacheLocal(type, target.SteamId, reason.Name, durationValue);

            var liveTarget = Utilities.GetPlayers()
                .FirstOrDefault(p => p.IsValid && p.SteamID == target.SteamId);

            if (type == "ban" && liveTarget is not null)
            {
                AdminPenalties.Kick(
                    liveTarget,
                    string.IsNullOrWhiteSpace(reason.Name) ? "Banned" : reason.Name);
            }
            else if (type == "mute" && liveTarget is not null)
            {
                AdminPenalties.CheckPlayer(liveTarget);
            }

            var durationLabel = duration?.Name ?? durationValue;
            var verb = type switch
            {
                "ban" => "banned",
                "mute" => "muted",
                "gag" => "gagged",
                _ => type,
            };
            Broadcast(
                $"{ChatColors.LightRed}{target.Name}{ChatColors.Default} was {verb} for {durationLabel} ({reason.Name}).");
            liveAdmin?.PrintToChat(
                $"{ChatColors.Green}[Admin] Successfully {verb} {target.Name} ({durationLabel}).");
        });
    }

    private static void Broadcast(string message)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid && !player.IsBot && !player.IsHLTV)
                player.PrintToChat($" {ChatColors.Grey}[Admin]{ChatColors.Default} {message}");
        }
    }
}
