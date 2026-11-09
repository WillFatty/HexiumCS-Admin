using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;

namespace HexiumCSAdmin;

internal static class TeamChrome
{
    public const string TeamT = "team-t";
    public const string TeamCt = "team-ct";

    public static void Apply(CCSCustomHudLayout hud, CCSPlayerController player, params string[] panelIds)
    {
        try
        {
            var isCt = ResolveTeam(player) == CsTeam.CounterTerrorist;

            foreach (var panelId in panelIds)
            {
                if (string.IsNullOrEmpty(panelId))
                    continue;

                hud.SetHasClassForPlayer(player, panelId, TeamT, !isCt);
                hud.SetHasClassForPlayer(player, panelId, TeamCt, isCt);
            }
        }
        catch
        {
            // Never block the menu if team chrome fails.
        }
    }

    public static CsTeam ResolveTeam(CCSPlayerController player)
    {
        return player.TeamNum switch
        {
            2 => CsTeam.Terrorist,
            3 => CsTeam.CounterTerrorist,
            _ when player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist => player.Team,
            _ => CsTeam.Terrorist
        };
    }
}
