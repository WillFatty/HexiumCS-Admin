using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace HexiumCSAdmin;

/// <summary>
/// Records a server-side demo (tv_record) for the whole match and uploads it to
/// InventorySim when the map ends, tagged with the same match ID used by
/// !calladmin tickets and chat logs.
/// </summary>
internal static class DemoRecorder
{
    private const int MaxFinalizeSeconds = 120;
    private const long MinDemoBytes = 100 * 1024;
    private const string SidecarSuffix = ".hexadmin.json";
    private const float PendingUploadRetrySeconds = 300f;

    private static BasePlugin? _plugin;
    private static AdminConfig _config = new();
    private static bool _recording;
    private static string _demoPrefix = "";
    private static string _csgoDir = "";
    private static DateTime _recordStartedUtc;
    private static string _mapName = "";
    private static int _startToken;
    private static Timer? _retryTimer;
    private static int _retryInFlight;

    public static void Initialize(BasePlugin plugin, AdminConfig config)
    {
        _plugin = plugin;
        _config = config;
        plugin.RegisterListener<Listeners.OnMapStart>(OnMapStart);
        plugin.RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        plugin.RegisterListener<Listeners.OnServerHibernationUpdate>(OnHibernationUpdate);
        RecordingIndicator.Initialize(plugin);

        // Hot reload / late load mid-map: the OnMapStart listener may have
        // already fired, so retry once the map name is available.
        ScheduleStartRecording();

        // Retry demos whose upload failed on a previous map / plugin load.
        var csgoDir = ResolveCsgoDir(Server.GameDirectory);
        _ = RetryPendingSafeAsync();
        _ = AppendUploadLogAsync(csgoDir,
            $"{DateTime.UtcNow:O} plugin initialized dir={csgoDir}");

        // Keep retrying pending uploads while the server runs so a site
        // outage doesn't have to wait for the next map change to recover.
        _retryTimer = plugin.AddTimer(PendingUploadRetrySeconds,
            () => _ = RetryPendingSafeAsync(), TimerFlags.REPEAT);
    }

    /// <summary>
    /// Server.GameDirectory points at the game root (…/game), while tv_record,
    /// sidecars and the upload log all live in the csgo subfolder (…/game/csgo).
    /// </summary>
    private static string ResolveCsgoDir(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            return gameDirectory;

        var csgoDir = Path.Combine(gameDirectory, "csgo");
        return Directory.Exists(csgoDir) ? csgoDir : gameDirectory;
    }

    public static void SetConfig(AdminConfig config) => _config = config;

    /// <summary>True while a tv_record demo is being captured.</summary>
    public static bool IsRecording => _recording;

    /// <summary>
    /// Manual toggle for the !record admin command. Returns the new recording
    /// state, or null when demo recording is not enabled on this server.
    /// </summary>
    public static bool? ToggleRecording()
    {
        if (!ApiConfigured)
            return null;

        if (_recording)
        {
            StopAndScheduleFinalize();
            return false;
        }

        StartRecording();
        return _recording;
    }

    /// <summary>
    /// Starts recording if not already recording. Returns the recording state,
    /// or null when demo recording is not enabled on this server.
    /// </summary>
    public static bool? StartRecordingNow()
    {
        if (!ApiConfigured)
            return null;

        if (_recording)
            return true;

        StartRecording();
        return _recording;
    }

    /// <summary>
    /// Stops the active recording (if any) and schedules finalize/upload.
    /// Returns true when a recording was actually stopped.
    /// </summary>
    public static bool StopRecording()
    {
        if (!_recording)
            return false;

        StopAndScheduleFinalize();
        return true;
    }

    public static void Shutdown()
    {
        if (_plugin is not null)
        {
            _plugin.RemoveListener<Listeners.OnMapStart>(OnMapStart);
            _plugin.RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
            _plugin.RemoveListener<Listeners.OnServerHibernationUpdate>(OnHibernationUpdate);
        }

        _retryTimer?.Kill();
        _retryTimer = null;

        StopAndScheduleFinalize();
        RecordingIndicator.Shutdown();

        _plugin = null;
    }

    /// <summary>Auto-recording gate: config flag plus InventorySim credentials.</summary>
    private static bool Enabled => _config.DemoRecordingEnabled && ApiConfigured;

    /// <summary>
    /// Manual commands (!record / !admin) only need InventorySim credentials —
    /// DemoRecordingEnabled gates the automatic per-match recording, not admin
    /// overrides.
    /// </summary>
    private static bool ApiConfigured =>
        !string.IsNullOrWhiteSpace(_config.ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.ApiKey);

    private static void OnMapStart(string mapName)
    {
        StopAndScheduleFinalize();

        _mapName = mapName;
        _demoPrefix = "";
        _csgoDir = "";
        ScheduleStartRecording();
        _ = RetryPendingSafeAsync();
    }

    private static void OnMapEnd() => StopAndScheduleFinalize();

    /// <summary>
    /// tv_record must not run until SourceTV has started for the new map. On a
    /// changelevel, OnMapStart fires first; recording immediately captures a
    /// 0.1 s demo and stops when SourceTV actually comes up. Delay the start
    /// and re-arm after hibernation, which also shuts SourceTV down.
    /// </summary>
    private static void ScheduleStartRecording()
    {
        if (_plugin is null)
            return;

        var token = ++_startToken;
        _plugin.AddTimer(3.0f, () =>
        {
            if (token != _startToken || !Enabled || _recording ||
                string.IsNullOrWhiteSpace(Server.MapName))
                return;

            StartRecording();
        });
    }

    private static void OnHibernationUpdate(bool isHibernating)
    {
        if (isHibernating)
        {
            // Hibernation stops SourceTV and completes the demo file. Finalize
            // what was recorded so it is not orphaned, then start fresh on wake.
            StopAndScheduleFinalize();
            return;
        }

        // Server woke up (first player joined): SourceTV restarted, so record
        // from here if the previous demo already ended.
        ++_startToken;
        ScheduleStartRecording();
    }

    private static void StopAndScheduleFinalize()
    {
        if (!_recording)
            return;

        _recording = false;
        RecordingIndicator.SetRecording(false);
        var match = (ChatLogger.CurrentMatchId, _mapName, _config.ServerName);
        var prefix = _demoPrefix;
        var csgoDir = _csgoDir;
        var startedUtc = _recordStartedUtc;
        _ = AppendUploadLogAsync(csgoDir,
            $"{DateTime.UtcNow:O} stop {prefix} match={match.CurrentMatchId} map={match._mapName}");
        Server.ExecuteCommand("tv_stoprecord");
        _ = FinalizeAndUploadAsync(match, prefix, csgoDir, startedUtc);
    }

    private static void StartRecording()
    {
        if (!ApiConfigured || _recording)
            return;

        try
        {
            StartRecordingCore();
        }
        catch (Exception ex)
        {
            Server.PrintToConsole(
                $"[HexiumCS-Admin] Demo recording failed to start: {ex.Message}");
        }
    }

    private static void StartRecordingCore()
    {
        var tvEnable = ConVar<bool>.Find("tv_enable");
        if (tvEnable is not null && !tvEnable.Value)
        {
            Server.PrintToConsole(
                "[HexiumCS-Admin] Demo recording skipped: tv_enable is false.");
            return;
        }

        var map = string.IsNullOrWhiteSpace(_mapName) ? Server.MapName : _mapName;
        if (string.IsNullOrWhiteSpace(map))
            return;
        _mapName = map;
        // tv_record appends .dem itself; the file lands in game/csgo/ root.
        _demoPrefix =
            $"hexadmin-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid().ToString("N")[..6]}";
        _csgoDir = ResolveCsgoDir(Server.GameDirectory);
        _recordStartedUtc = DateTime.UtcNow;

        Server.ExecuteCommand("tv_stoprecord");
        if (ConVar<bool>.Find("tv_record_immediate") is not null)
            Server.ExecuteCommand("tv_record_immediate 1");
        Server.ExecuteCommand($"tv_record {_demoPrefix}");
        _recording = true;
        RecordingIndicator.SetRecording(true);
        Server.PrintToConsole(
            $"[HexiumCS-Admin] Recording match demo {_demoPrefix}.dem on {map}.");
        _ = AppendUploadLogAsync(_csgoDir,
            $"{DateTime.UtcNow:O} start {_demoPrefix} map={map} dir={_csgoDir}");
    }

    /// <summary>
    /// Locates the .dem tv_record produced. The engine may append .dem, keep
    /// or strip a subdirectory, so match on the unique prefix rather than an
    /// exact path.
    /// </summary>
    private static string? ResolveDemoFile(string prefix, string csgoDir, DateTime startedUtc)
    {
        string? best = null;
        var bestWrite = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(csgoDir, prefix + "*"))
        {
            DateTime write;
            try
            {
                write = File.GetLastWriteTimeUtc(file);
            }
            catch
            {
                continue;
            }

            // Ignore stale files from an earlier run with the same prefix.
            if (write < startedUtc.AddSeconds(-30))
                continue;
            if (write > bestWrite)
            {
                bestWrite = write;
                best = file;
            }
        }
        return best;
    }

    /// <summary>
    /// Waits for the .dem file to finish flushing, uploads it, then removes the
    /// local copy on success. Runs without plugin timers so it survives map
    /// change and plugin unload. All progress is appended to
    /// hexadmin-upload.log because Server.NextFrame never runs while the
    /// server is hibernating between maps.
    /// </summary>
    private static async Task FinalizeAndUploadAsync(
        (string MatchId, string Map, string ServerName) match,
        string demoPrefix,
        string csgoDir,
        DateTime startedUtc)
    {
        try
        {
            await FinalizeAndUploadCoreAsync(match, demoPrefix, csgoDir, startedUtc);
        }
        catch (Exception ex)
        {
            await AppendUploadLogAsync(csgoDir,
                $"{DateTime.UtcNow:O} finalize {demoPrefix} EXCEPTION {ex}");
        }
    }

    private static async Task FinalizeAndUploadCoreAsync(
        (string MatchId, string Map, string ServerName) match,
        string demoPrefix,
        string csgoDir,
        DateTime startedUtc)
    {
        var demoPath = ResolveDemoFile(demoPrefix, csgoDir, startedUtc);
        if (demoPath is null)
        {
            // The file may appear a moment after tv_stoprecord.
            for (var i = 0; i < MaxFinalizeSeconds && demoPath is null; i++)
            {
                await Task.Delay(1000);
                demoPath = ResolveDemoFile(demoPrefix, csgoDir, startedUtc);
            }
        }

        if (demoPath is null)
        {
            await AppendUploadLogAsync(csgoDir,
                $"{DateTime.UtcNow:O} finalize {demoPrefix} demo file not found");
            Server.NextFrame(() => Server.PrintToConsole(
                "[HexiumCS-Admin] Demo file was not created; check tv_enable " +
                "and the server console for tv_record errors."));
            return;
        }

        await AppendUploadLogAsync(csgoDir,
            $"{DateTime.UtcNow:O} finalize {demoPrefix} found {demoPath}");

        // Wait until the file size stops growing (flush complete).
        long lastSize = -1;
        for (var i = 0; i < MaxFinalizeSeconds; i++)
        {
            await Task.Delay(1000);
            long size;
            try
            {
                size = new FileInfo(demoPath).Length;
            }
            catch
            {
                size = -1;
            }

            if (size > 0 && size == lastSize)
                break;
            lastSize = size;
        }

        // Server hibernation stops SourceTV almost immediately, leaving tiny
        // junk demos — don't upload those.
        long demoSize;
        try
        {
            demoSize = new FileInfo(demoPath).Length;
        }
        catch
        {
            demoSize = 0;
        }

        if (demoSize < MinDemoBytes)
        {
            await AppendUploadLogAsync(csgoDir,
                $"{DateTime.UtcNow:O} finalize {demoPrefix} skipped " +
                $"{Path.GetFileName(demoPath)} ({demoSize} bytes)");
            Server.NextFrame(() => Server.PrintToConsole(
                $"[HexiumCS-Admin] Skipping demo {Path.GetFileName(demoPath)} " +
                $"({demoSize} bytes — server hibernated)."));
            try
            {
                File.Delete(demoPath);
            }
            catch
            {
                // Leftover junk file is harmless.
            }
            return;
        }

        var fileName = Path.GetFileName(demoPath);
        await AppendUploadLogAsync(csgoDir,
            $"{DateTime.UtcNow:O} finalize {fileName} uploading " +
            $"match={match.MatchId} map={match.Map} size={demoSize}");
        var upload = await InventorySimClient.UploadDemoAsync(
            _config.ApiBaseUrl,
            _config.ApiKey,
            match.ServerName,
            match.MatchId,
            match.Map,
            fileName,
            demoPath);
        var ok = upload.Success;
        await AppendUploadLogAsync(csgoDir,
            $"{DateTime.UtcNow:O} finalize {fileName} upload result: " +
            (ok ? "OK" : upload.Error ?? "failed"));

        // Delete on the thread pool — Server.NextFrame never runs while the
        // server is hibernating (e.g. between changelevels).
        if (ok)
        {
            try
            {
                File.Delete(demoPath);
            }
            catch
            {
                // Logged below if the frame ever runs.
            }
        }
        else
        {
            // Leave a sidecar so the next map load / plugin load retries.
            await WritePendingSidecarAsync(csgoDir, fileName, match, upload.Error);
        }

        Server.NextFrame(() =>
        {
            if (ok)
            {
                Server.PrintToConsole(
                    $"[HexiumCS-Admin] Uploaded demo {fileName} for match {match.MatchId}.");
            }
            else
            {
                Server.PrintToConsole(
                    $"[HexiumCS-Admin] Demo upload failed for {fileName} " +
                    $"({upload.Error}); file kept at " + demoPath);
            }
        });
    }

    private static async Task WritePendingSidecarAsync(
        string csgoDir,
        string fileName,
        (string MatchId, string Map, string ServerName) match,
        string? error)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                fileName,
                matchId = match.MatchId,
                map = match.Map,
                serverName = match.ServerName,
                error
            });
            await File.WriteAllTextAsync(
                Path.Combine(csgoDir, fileName + SidecarSuffix), json);
        }
        catch
        {
            // Without the sidecar the file is simply orphaned on disk.
        }
    }

    /// <summary>
    /// Runs <see cref="RetryPendingUploadsAsync"/> at most once at a time —
    /// the map-start hook and the periodic timer can overlap.
    /// </summary>
    private static async Task RetryPendingSafeAsync()
    {
        if (Interlocked.Exchange(ref _retryInFlight, 1) == 1)
            return;

        try
        {
            await RetryPendingUploadsAsync(ResolveCsgoDir(Server.GameDirectory));
        }
        finally
        {
            Interlocked.Exchange(ref _retryInFlight, 0);
        }
    }

    /// <summary>
    /// Retries uploads for demos whose previous attempt failed. Failed uploads
    /// leave a *.hexadmin.json sidecar next to the .dem with the match metadata,
    /// so this works across map changes and plugin reloads.
    /// </summary>
    private static async Task RetryPendingUploadsAsync(string csgoDir)
    {
        if (!ApiConfigured || string.IsNullOrWhiteSpace(csgoDir))
            return;

        string[] sidecars;
        try
        {
            sidecars = Directory.GetFiles(csgoDir, "*" + SidecarSuffix);
        }
        catch
        {
            return;
        }

        foreach (var sidecar in sidecars)
        {
            string fileName, matchId, map, serverName;
            try
            {
                await using var stream = File.OpenRead(sidecar);
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                fileName = root.GetProperty("fileName").GetString() ?? "";
                matchId = root.GetProperty("matchId").GetString() ?? "";
                map = root.GetProperty("map").GetString() ?? "";
                serverName = root.GetProperty("serverName").GetString() ?? "";
            }
            catch
            {
                continue;
            }

            // Guard against tampered sidecars pointing outside csgo dir.
            fileName = Path.GetFileName(fileName);
            if (!fileName.StartsWith("hexadmin-") ||
                !fileName.EndsWith(".dem") ||
                matchId.Length == 0)
            {
                try { File.Delete(sidecar); } catch { }
                continue;
            }

            var demoPath = Path.Combine(csgoDir, fileName);
            if (!File.Exists(demoPath))
            {
                try { File.Delete(sidecar); } catch { }
                continue;
            }

            var upload = await InventorySimClient.UploadDemoAsync(
                _config.ApiBaseUrl,
                _config.ApiKey,
                serverName,
                matchId,
                map,
                fileName,
                demoPath);

            await AppendUploadLogAsync(csgoDir,
                $"{DateTime.UtcNow:O} retry {fileName} match={matchId} " +
                $"map={map} -> {(upload.Success ? "OK" : upload.Error ?? "failed")}");
            if (!upload.Success)
                continue;

            try
            {
                File.Delete(demoPath);
                File.Delete(sidecar);
            }
            catch
            {
                // Files remain; next retry will try again.
            }

            Server.NextFrame(() => Server.PrintToConsole(
                $"[HexiumCS-Admin] Uploaded pending demo {fileName} for match {matchId}."));
        }
    }

    /// <summary>
    /// Appends a line to hexadmin-upload.log next to the demos. Unlike
    /// Server.PrintToConsole this survives server hibernation, so upload
    /// results are always recoverable from disk.
    /// </summary>
    private static async Task AppendUploadLogAsync(string csgoDir, string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(csgoDir))
                return;
            await File.AppendAllTextAsync(
                Path.Combine(csgoDir, "hexadmin-upload.log"), line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break the upload path.
        }
    }
}
