# HexiumCS-Admin

CounterStrikeSharp plugin with Panorama menus for `!ban`, `!kick`, `!mute`, and `!gag`.

Bans, mutes, and gags are stored on InventorySim and synced to every server running this plugin (or HexiumCS-CallAdmin for bans).

## Requirements

- CounterStrikeSharp
- The `hexiumcs_ui` Panorama addon (reuses `hexium_calladmin` layout)
- An InventorySim `ApiCredential` whose `scope` includes `admin`
- CSS admin permissions:
  - `@css/ban` → `!ban`
  - `@css/kick` → `!kick`
  - `@css/chat` → `!mute` / `!gag`

## Install

```
addons/counterstrikesharp/plugins/HexiumCS-Admin/HexiumCS-Admin.dll
```

Edit `addons/counterstrikesharp/configs/plugins/HexiumCS-Admin/HexiumCS-Admin.json`:

```json
{
  "ApiBaseUrl": "https://your-inventorysim.example",
  "ApiKey": "<api credential with admin scope>",
  "ServerName": "EU West #1",
  "PenaltySyncIntervalSeconds": 5,
  "DemoRecordingEnabled": true
}
```

On InventorySim, run the Prisma migration so `CallAdminBan` / `AdminCommPenalty` / `AdminDemo` exist, then grant the credential the `admin` scope from `/admin`.

## Match demos

When `DemoRecordingEnabled` is true (and `ApiBaseUrl` / `ApiKey` are set), the plugin records a server-side demo via `tv_record` for the whole match — requires `tv_enable 1`. On map end the `.dem` file (written under `game/csgo/hexadmin/`) is uploaded to `POST /api/admin/demos` on InventorySim with the same match ID used by `!calladmin` tickets and chat logs, then deleted locally. Failed uploads keep the file on disk. Demos can be downloaded by admins from `/admin` → Demos.

## Commands

| Command | Permission | Flow |
|---------|------------|------|
| `!admin` / `css_admin` | any of ban/kick/chat | Action → player → duration → reason |
| `!ban` / `css_ban` | `@css/ban` | Player → duration → reason → POST ban + kick |
| `!kick` / `css_kick` | `@css/kick` | Player → reason → kick (local only) |
| `!mute` / `css_mute` | `@css/chat` | Player → duration → reason → voice mute |
| `!gag` / `css_gag` | `@css/chat` | Player → duration → reason → block text chat |

## Build

```bash
dotnet build -c Release
```

Output: `bin/Release/addons/counterstrikesharp/plugins/HexiumCS-Admin/`
