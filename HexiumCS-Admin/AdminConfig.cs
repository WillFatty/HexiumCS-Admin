using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace HexiumCSAdmin;

public class AdminReason
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Description")]
    public string Description { get; set; } = "";
}

public class AdminDurationOption
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Description")]
    public string Description { get; set; } = "";

    /// <summary>Duration token sent to InventorySim: 1h, 6h, 24h, 3d, 7d, permanent.</summary>
    [JsonPropertyName("Value")]
    public string Value { get; set; } = "1h";
}

public class AdminConfig : BasePluginConfig
{
    [JsonPropertyName("ApiBaseUrl")]
    public string ApiBaseUrl { get; set; } = "";

    /// <summary>ApiCredential apiKey with the "admin" scope on InventorySim.</summary>
    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "";

    /// <summary>
    /// Records a tv_record demo for each match and uploads it to InventorySim
    /// on map end, tagged with the chat-log / call-admin match ID. Requires
    /// ApiBaseUrl + ApiKey and tv_enable 1 on the server.
    /// </summary>
    [JsonPropertyName("DemoRecordingEnabled")]
    public bool DemoRecordingEnabled { get; set; } = true;

    [JsonPropertyName("PenaltySyncIntervalSeconds")]
    public int PenaltySyncIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("Durations")]
    public List<AdminDurationOption> Durations { get; set; } =
    [
        new() { Name = "1 hour", Description = "Temporary", Value = "1h" },
        new() { Name = "6 hours", Description = "Temporary", Value = "6h" },
        new() { Name = "1 day", Description = "Temporary", Value = "24h" },
        new() { Name = "3 days", Description = "Temporary", Value = "3d" },
        new() { Name = "1 week", Description = "Temporary", Value = "7d" },
        new() { Name = "Permanent", Description = "Does not expire", Value = "permanent" },
    ];

    [JsonPropertyName("CallAdminReasons")]
    public List<AdminReason> CallAdminReasons { get; set; } =
    [
        new() { Name = "Cheating", Description = "Suspected cheats or hacks" },
        new() { Name = "Toxicity", Description = "Abusive chat or behavior" },
        new() { Name = "Griefing", Description = "Intentionally ruining the game" },
        new() { Name = "Need help", Description = "General assistance request" },
        new() { Name = "Other", Description = "See chat / previous context" },
    ];

    [JsonPropertyName("BanReasons")]
    public List<AdminReason> BanReasons { get; set; } =
    [
        new() { Name = "Cheating", Description = "Aimbot, wallhack, or other cheats" },
        new() { Name = "Toxicity", Description = "Abusive chat, hate speech, or threats" },
        new() { Name = "Griefing", Description = "Intentionally ruining the game" },
        new() { Name = "Other", Description = "See chat / previous warnings" },
    ];

    [JsonPropertyName("KickReasons")]
    public List<AdminReason> KickReasons { get; set; } =
    [
        new() { Name = "Disruptive", Description = "Disturbing other players" },
        new() { Name = "AFK", Description = "Idle / not playing" },
        new() { Name = "Other", Description = "See chat" },
    ];

    [JsonPropertyName("MuteReasons")]
    public List<AdminReason> MuteReasons { get; set; } =
    [
        new() { Name = "Mic spam", Description = "Voice spam or noise" },
        new() { Name = "Toxicity", Description = "Abusive voice chat" },
        new() { Name = "Other", Description = "See chat" },
    ];

    [JsonPropertyName("GagReasons")]
    public List<AdminReason> GagReasons { get; set; } =
    [
        new() { Name = "Chat spam", Description = "Text spam" },
        new() { Name = "Toxicity", Description = "Abusive text chat" },
        new() { Name = "Other", Description = "See chat" },
    ];
}
