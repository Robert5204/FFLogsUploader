using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FFLogsPlugin.Helpers;
using FFLogsPlugin.Models;
using Microsoft.ClearScript.V8;

namespace FFLogsPlugin.Services;

/// <summary>
/// Manages the FFLogs parser using an embedded V8 JavaScript engine (ClearScript).
/// No Node.js subprocess required.
///
/// Thread safety: All V8 engine calls are serialized through <see cref="engineLock"/>.
/// The engine must not be accessed concurrently — V8ScriptEngine is single-threaded.
/// </summary>
public class ParserService : IDisposable
{
    private const string FFLOGS_URL = "https://www.fflogs.com";

    // Configurable timing constants
    private const int LiveLogPollIntervalMs = 500;
    private const int FightEndDelayMs = 5000;
    private const int FileCheckIntervalSeconds = 10;

    // Wipe director code — distinct from phase-transition codes; triggers call-wipe
    private const string WipeDirectorCode = "40000011";

    // Parser page query parameters
    private const string ParserMetersEnabled = "false";
    private const string ParserLiveFightDataEnabled = "false";

    /// <summary>
    /// Director command codes that indicate a fight has ended.
    /// These are FFXIV network opcodes from log line type 33 (NetworkDirector).
    /// </summary>
    private static readonly HashSet<string> FightEndDirectorCodes = new()
    {
        "40000011", // Wipe cleanup
        "40000002", // Victory / duty complete
        "40000003", // Duty complete (alternative)
        "40000005", // Duty complete (alternative)
    };

    private readonly Plugin plugin;
    private string? parserBundlePath;
    private V8ScriptEngine? engine;
    private readonly List<JsonElement> ipcMessages = new();
    private readonly object ipcLock = new();
    private string? currentReportCode;

    // Serializes all V8 engine access — V8ScriptEngine is not thread-safe
    private readonly SemaphoreSlim engineLock = new(1, 1);
    private bool disposed;

    // Use the authenticated HttpClient from FFLogsService
    private HttpClient HttpClient => plugin.FFLogsService.HttpClient;

    public ParserService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        StopParser();
        engineLock.Dispose();
        GC.SuppressFinalize(this);
    }

    ~ParserService()
    {
        StopParser();
    }

    private void StopParser()
    {
        if (engine != null)
        {
            try { engine.Dispose(); } catch { }
            engine = null;
        }
        currentReportCode = null;
    }

    /// <summary>
    /// Inform the parser of the current report code.
    /// Called from FFLogsService after a report is created during live logging,
    /// so that subsequent collect-master-info calls can pass the correct code.
    /// </summary>
    public void SetReportCode(string reportCode)
    {
        currentReportCode = reportCode;
        SendMessage(new { message = "set-report-code", id = 0, reportCode });
    }

    /// <summary>
    /// Ensures the parser bundle is downloaded, validated, and cached.
    /// The cached bundle stores only the raw parser + glue code (no Node.js wrapper).
    /// </summary>
    private async Task EnsureParserAsync()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "FFLogsPlugin"
        );
        Directory.CreateDirectory(cacheDir);
        parserBundlePath = Path.Combine(cacheDir, "fflogs_parser_v8.js");

        // If cached and fresh (< 24 hours), validate and use it
        if (File.Exists(parserBundlePath) && File.GetLastWriteTimeUtc(parserBundlePath) > DateTime.UtcNow.AddHours(-24))
        {
            var cachedSize = new FileInfo(parserBundlePath).Length;
            if (cachedSize > 10000)
            {
                Plugin.Log.Information($"Using cached parser: {parserBundlePath}");
                return;
            }

            Plugin.Log.Warning($"Cached parser bundle appears invalid (size={cachedSize}), re-downloading...");
            File.Delete(parserBundlePath);
        }

        Plugin.Log.Information("Downloading FFLogs parser...");
        var tempPath = parserBundlePath + ".tmp";

        try
        {
            // Fetch the parser page
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var parserPageUrl = $"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&gameContentDetectionEnabled=false&metersEnabled={ParserMetersEnabled}&liveFightDataEnabled={ParserLiveFightDataEnabled}";

            var html = await HttpClient.GetStringAsync(parserPageUrl);

            // Extract parser URL from HTML
            var match = Regex.Match(html, @"src=""([^""]+parser-ff[^""]+)""");
            if (!match.Success)
            {
                throw new Exception("Could not find parser URL in response");
            }

            var parserUrl = match.Groups[1].Value;
            Plugin.Log.Debug($"Found parser URL: {parserUrl}");

            // Download the parser
            var parserCode = await HttpClient.GetStringAsync(parserUrl);

            // Extract inline glue code
            var scriptBlocks = Regex.Matches(html, @"<script type=""text/javascript"">([\s\S]*?)</script>");
            var glueCode = "";
            foreach (Match m in scriptBlocks)
            {
                if (m.Groups[1].Value.Contains("ipcCollectFights"))
                {
                    glueCode = m.Groups[1].Value;
                    break;
                }
            }

            // Bundle parser + glue only (no Node.js wrapper needed)
            var fullBundle = parserCode + "\n\n" + glueCode;

            // Validate
            if (fullBundle.Length < 10000 || !fullBundle.Contains("ipcCollectFights"))
            {
                throw new Exception($"Downloaded parser bundle appears invalid (size={fullBundle.Length}, missing expected markers). " +
                                    "This may be a partial download or a Cloudflare error page.");
            }

            if (fullBundle.Contains("<script") || fullBundle.Contains("</script>"))
            {
                throw new Exception("Parser bundle contains raw HTML tags — " +
                    "the FFLogs parser page structure may have changed. " +
                    "Please report this issue.");
            }

            await File.WriteAllTextAsync(tempPath, fullBundle);

            if (File.Exists(parserBundlePath))
                File.Delete(parserBundlePath);
            File.Move(tempPath, parserBundlePath);

            Plugin.Log.Information($"Parser downloaded and cached ({fullBundle.Length} bytes)");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Plugin.Log.Error(ex, "Failed to download parser");
            throw new Exception($"Failed to download parser: {ex.Message}");
        }
    }

    /// <summary>
    /// Browser environment shims for the FFLogs parser JavaScript.
    /// Provides mock window, document, and Web API objects that the parser expects.
    /// </summary>
    private const string BrowserShimsJs = @"
        var window = {
            location: { search: '?id=1&metersEnabled=false&liveFightDataEnabled=false' },
            addEventListener: function(type, listener) { if (type === 'message') { this._messageListener = listener; } },
            removeEventListener: function() {},
            dispatchEvent: function() { return true; },
            _messageListener: null
        };
        var document = {
            createElement: function() { return { style: {}, appendChild: function(){}, addEventListener: function(){} }; },
            getElementsByTagName: function() { return [{ appendChild: function(){} }]; },
            getElementById: function() { return null; },
            querySelector: function() { return null; },
            querySelectorAll: function() { return []; },
            body: { appendChild: function(){} },
            head: { appendChild: function(){} },
            readyState: 'complete',
            addEventListener: function() {},
            removeEventListener: function() {}
        };
        var self = window;
        var navigator = { userAgent: 'FFLogsPlugin/1.0', platform: 'Win32' };
        var location = window.location;
        var localStorage = { getItem: function() { return null; }, setItem: function() {}, removeItem: function() {} };
        var sessionStorage = localStorage;

        var _timerId = 0;
        var _pendingTimeouts = [];
        function setTimeout(fn, delay) {
            var id = ++_timerId;
            if (typeof fn === 'function') _pendingTimeouts.push(fn);
            return id;
        }
        function setInterval() { return ++_timerId; }
        function clearTimeout() {}
        function clearInterval() {}
        var performance = { now: function() { return Date.now(); } };
        function fetch() { throw new Error('fetch not supported'); }

        // Drain deferred callbacks — called from C# after each message dispatch
        function __drainTimeouts() {
            var safety = 1000;
            while (_pendingTimeouts.length > 0 && safety-- > 0) {
                var batch = _pendingTimeouts.splice(0, _pendingTimeouts.length);
                for (var i = 0; i < batch.length; i++) {
                    try { batch[i](); } catch(e) {}
                }
            }
        }

        var URLSearchParams = function(init) {
            this._params = {};
            if (typeof init === 'string') {
                var str = init.charAt(0) === '?' ? init.substring(1) : init;
                var pairs = str.split('&');
                for (var i = 0; i < pairs.length; i++) {
                    var kv = pairs[i].split('=');
                    if (kv.length === 2) this._params[decodeURIComponent(kv[0])] = decodeURIComponent(kv[1]);
                }
            }
        };
        URLSearchParams.prototype.get = function(k) { return this._params.hasOwnProperty(k) ? this._params[k] : null; };
        URLSearchParams.prototype.set = function(k, v) { this._params[k] = v; };
        URLSearchParams.prototype.has = function(k) { return this._params.hasOwnProperty(k); };

        // IPC output — calls back into C# via host object
        window.sendToHost = function(channel, id, event, data) {
            __ipc.capture(JSON.stringify({ channel: channel, id: id, data: data }));
        };

        // Helper to dispatch a message to the parser (replaces stdin JSON protocol)
        function __dispatchMessage(msgJson) {
            if (!window._messageListener) throw new Error('Parser not initialized: no message listener');
            var msg = JSON.parse(msgJson);
            window._messageListener({
                data: msg,
                source: { postMessage: function(r) { __ipc.capture(JSON.stringify(r)); } },
                origin: 'emulator'
            });
            __drainTimeouts();
        }
    ";

    private async Task StartParserAsync()
    {
        await EnsureParserAsync();

        // Dispose any existing engine
        StopParser();

        Plugin.Log.Information("Starting V8 parser engine...");
        engine = new V8ScriptEngine();

        // Register IPC callback host object
        engine.AddHostObject("__ipc", new IpcHost(this));

        // Set up browser shims
        engine.Execute(BrowserShimsJs);

        // Load the parser bundle
        var bundleCode = await File.ReadAllTextAsync(parserBundlePath!);
        engine.Execute(bundleCode);

        // Drain any timeouts queued during parser initialization
        engine.Execute("__drainTimeouts()");

        // Verify the parser registered its message listener
        var hasListener = engine.Evaluate("!!window._messageListener");
        if (hasListener is not true)
            throw new Exception("Parser did not register a message listener — bundle may be invalid");

        Plugin.Log.Information("V8 parser engine ready");
    }

    /// <summary>
    /// Send a message to the parser (fire-and-forget — responses are not collected).
    /// Must be called while holding <see cref="engineLock"/>, or from a method that does.
    /// </summary>
    private void SendMessageCore(object message)
    {
        if (engine == null)
            throw new InvalidOperationException("Parser engine is not running");

        var json = JsonSerializer.Serialize(message);
        if (!json.StartsWith("{\"message\":\"parse-lines\""))
            Plugin.Log.Debug($"[Parser] Sending: {json[..Math.Min(200, json.Length)]}...");

        engine.Execute($"__dispatchMessage({JsonSerializer.Serialize(json)})");
    }

    /// <summary>
    /// Send a message to the parser without collecting responses.
    /// Thread-safe — acquires the engine lock.
    /// </summary>
    private void SendMessage(object message)
    {
        engineLock.Wait();
        try
        {
            SendMessageCore(message);
        }
        finally
        {
            engineLock.Release();
        }
    }

    /// <summary>
    /// Send a message and return all IPC responses collected during execution.
    /// Thread-safe — acquires the engine lock.
    /// </summary>
    private List<JsonElement> SendMessageAndCollect(object message)
    {
        engineLock.Wait();
        try
        {
            lock (ipcLock)
            {
                ipcMessages.Clear();
            }

            SendMessageCore(message);

            lock (ipcLock)
            {
                return new List<JsonElement>(ipcMessages);
            }
        }
        finally
        {
            engineLock.Release();
        }
    }

    /// <summary>
    /// Find the first IPC response containing a specific key.
    /// Checks both wrapped (data.key) and direct (key) formats.
    /// Returns the unwrapped element containing the key.
    /// </summary>
    private static JsonElement? FindResponseWithKey(List<JsonElement> responses, string key)
    {
        foreach (var msg in responses)
        {
            if (msg.TryGetProperty("data", out var d) && d.TryGetProperty(key, out _))
                return d;
            if (msg.TryGetProperty(key, out _))
                return msg;
        }
        return null;
    }

    /// <summary>
    /// Process a log file using the embedded V8 parser.
    /// </summary>
    public async Task<(string masterData, List<FightData> fights, long startTime, long endTime)> ProcessLogAsync(string logPath, string reportCode, int region)
    {
        await StartParserAsync();

        var fights = new List<FightData>();
        long startTime = 0;
        long endTime = 0;
        string masterStr = "";

        try
        {
            Plugin.Log.Information($"[Parser] Processing log: {Path.GetFileName(logPath)}");

            var lines = await LogFileHelper.ReadAllLinesSharedAsync(logPath);
            Plugin.Log.Debug($"[Parser] Read {lines.Length} lines");

            var regionStr = ((FFLogsRegion)region).ToCode();

            // Set report code
            SendMessage(new { message = "set-report-code", id = 0, reportCode });

            // Parse lines — run on a thread pool thread to avoid blocking the async context
            // since V8 execution is synchronous and large files can take several seconds
            await Task.Run(() => SendParseLines(new List<string>(lines), regionStr, 0));

            // Collect fights
            var fightsResponses = SendMessageAndCollect(new
            {
                message = "collect-fights",
                id = 2,
                pushFightIfNeeded = false,
                scanningOnly = false
            });

            var fightsResult = FindResponseWithKey(fightsResponses, "fights");

            if (fightsResult.HasValue)
            {
                Plugin.Log.Debug($"[Parser] Got fights result with {(fightsResult.Value.TryGetProperty("fights", out var f) ? f.GetArrayLength() : 0)} fights");
            }
            else
            {
                Plugin.Log.Warning("[Parser] No fights result received!");
            }

            // Collect master info
            var masterResponses = SendMessageAndCollect(new
            {
                message = "collect-master-info",
                id = 3,
                reportCode
            });

            var masterResult = FindResponseWithKey(masterResponses, "actorsString");

            // Extract fights
            if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
            {
                var fr = fightsResult.Value;
                var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
                var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;

                int bulkIdx = 0;
                foreach (var fight in fightsArray.EnumerateArray())
                {
                    var fd = new FightData
                    {
                        Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                        StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                        EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                        EventsString = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "",
                        LogVersion = logVer,
                        GameVersion = gameVer
                    };
                    fights.Add(fd);
                    LogFightDiagnostic("BULK", bulkIdx + 1, fd, fight);
                    bulkIdx++;
                }

                startTime = fr.TryGetProperty("startTime", out var st) ? st.GetInt64() : 0;
                endTime = fr.TryGetProperty("endTime", out var et) ? et.GetInt64() : 0;
            }

            Plugin.Log.Information($"[Parser] Extracted {fights.Count} fights");
            if (masterResult.HasValue)
            {
                masterStr = BuildMasterTableString(fightsResult, masterResult.Value);
            }
        }
        finally
        {
            StopParser();
        }

        return (masterStr, fights, startTime, endTime);
    }

    /// <summary>
    /// Builds the master table string that FFLogs expects for report uploads.
    ///
    /// Format (proprietary FFLogs format):
    ///   Line 1: "{logVersion}|{gameVersion}|{logFileDetails}"
    ///   Then for each section (in strict order):
    ///     - Line: count of entries
    ///     - Lines: the entries themselves
    ///
    /// Section order: actorsString, abilitiesString, tuplesString, petsString
    /// </summary>
    private string BuildMasterTableString(JsonElement? fightsData, JsonElement masterData)
    {
        var parts = new List<string>();

        var logVersion = 72;
        var gameVersion = 1;
        var logFileDetails = "";

        if (fightsData.HasValue)
        {
            var fd = fightsData.Value;
            logVersion = fd.TryGetProperty("logVersion", out var lv) ? lv.GetInt32() : 72;
            gameVersion = fd.TryGetProperty("gameVersion", out var gv) ? gv.GetInt32() : 1;
            logFileDetails = fd.TryGetProperty("logFileDetails", out var lfd) ? lfd.GetString() ?? "" : "";
        }

        parts.Add($"{logVersion}|{gameVersion}|{logFileDetails}");

        string[] sectionKeys = { "actorsString", "abilitiesString", "tuplesString", "petsString" };

        foreach (var key in sectionKeys)
        {
            var value = "";
            if (masterData.TryGetProperty(key, out var prop))
            {
                value = prop.GetString() ?? "";
            }

            var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Where(l => !string.IsNullOrWhiteSpace(l))
                             .ToList();

            parts.Add(lines.Count.ToString());
            parts.AddRange(lines);
        }

        var result = string.Join("\n", parts);
        Plugin.Log.Debug($"[Parser] Master table: {result.Length} chars, header: {parts[0]}");
        return result;
    }

    /// <summary>
    /// Extracts the last event's relative timestamp (first pipe field) from an
    /// eventsString buffer ("73|1\n{count}\n{ts}|..."). Returns false if the
    /// buffer contains no event rows — used to skip empty fights produced by a
    /// force-push when nothing was actually recorded.
    /// </summary>
    private static bool TryGetLastEventRelativeTime(string eventsString, out long lastRelativeMs)
    {
        lastRelativeMs = 0;
        if (string.IsNullOrEmpty(eventsString)) return false;

        int end = eventsString.Length;
        while (end > 0 && (eventsString[end - 1] == '\n' || eventsString[end - 1] == '\r'))
            end--;
        if (end == 0) return false;

        int lineStart = eventsString.LastIndexOf('\n', end - 1) + 1;
        int pipe = eventsString.IndexOf('|', lineStart, end - lineStart);
        if (pipe < 0) return false;

        // Skip header line ("73|1") and count line — both precede any event row.
        // The header line's first field is the log version (2-digit), which still
        // parses as a number but we reject it by requiring the count-line separator.
        int firstNewline = eventsString.IndexOf('\n');
        int secondNewline = firstNewline >= 0 ? eventsString.IndexOf('\n', firstNewline + 1) : -1;
        if (secondNewline < 0 || lineStart <= secondNewline) return false;

        return long.TryParse(eventsString.AsSpan(lineStart, pipe - lineStart), out lastRelativeMs);
    }

    /// <summary>
    /// Dumps a fight's identifying fields so we can diff a bulk-upload run against a
    /// bulk/live run for the same log. If the server-side result differs across runs
    /// but this dump is identical, the bug is upload-side (or server-side merging);
    /// if this dump differs, the parser produced different output for the two paths.
    /// </summary>
    private static void LogFightDiagnostic(string tag, int segmentIndex, FightData fd, JsonElement rawFight)
    {
        long? dungeonId = null, dungeonPullId = null;
        string? boss = null, dungeonName = null;
        int? lastPhase = null;
        bool? kill = null;

        if (rawFight.TryGetProperty("boss", out var bossEl) && bossEl.ValueKind == JsonValueKind.String)
            boss = bossEl.GetString();
        if (rawFight.TryGetProperty("dungeonId", out var dId) && dId.ValueKind == JsonValueKind.Number)
            dungeonId = dId.GetInt64();
        if (rawFight.TryGetProperty("dungeonPullId", out var dpId) && dpId.ValueKind == JsonValueKind.Number)
            dungeonPullId = dpId.GetInt64();
        if (rawFight.TryGetProperty("dungeonName", out var dn) && dn.ValueKind == JsonValueKind.String)
            dungeonName = dn.GetString();
        if (rawFight.TryGetProperty("lastPhase", out var lp) && lp.ValueKind == JsonValueKind.Number)
            lastPhase = lp.GetInt32();
        if (rawFight.TryGetProperty("kill", out var k) && (k.ValueKind == JsonValueKind.True || k.ValueKind == JsonValueKind.False))
            kill = k.GetBoolean();

        var events = fd.EventsString ?? "";
        var evLen = events.Length;
        var evHead = evLen > 0 ? events[..Math.Min(160, evLen)].Replace('\n', ' ') : "";
        var evTail = evLen > 160 ? events[Math.Max(0, evLen - 160)..].Replace('\n', ' ') : "";
        int evLineCount = 0;
        for (int i = 0; i < evLen; i++) if (events[i] == '\n') evLineCount++;

        Plugin.Log.Information(
            $"[{tag}] Fight#{segmentIndex} name={fd.Name} start={fd.StartTime} end={fd.EndTime} " +
            $"dur={fd.EndTime - fd.StartTime}ms boss={boss} dungeonId={dungeonId} " +
            $"pullId={dungeonPullId} dungeonName={dungeonName} lastPhase={lastPhase} kill={kill} " +
            $"eventsLen={evLen} eventsLineCount={evLineCount}");
        Plugin.Log.Information($"[{tag}] Fight#{segmentIndex} eventsHead: {evHead}");
        if (evTail.Length > 0)
            Plugin.Log.Information($"[{tag}] Fight#{segmentIndex} eventsTail: {evTail}");
    }

    /// <summary>
    /// Send a batch of lines to the parser with cumulative byte position.
    /// The official FFLogs uploader always passes logFilePosition — the parser
    /// uses it internally for per-encounter state tracking.
    /// </summary>
    private void SendParseLines(List<string> lines, string regionStr, long position)
    {
        SendMessage(new
        {
            message = "parse-lines",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            lines,
            scanning = false,
            selectedRegion = regionStr,
            raidsToUpload = Array.Empty<object>(),
            logFilePosition = position
        });
    }

    private void SendCallWipe()
    {
        SendMessage(new { message = "call-wipe", id = 0 });
    }

    private void SendSetLiveLoggingStartTime(long startTimeMs)
    {
        SendMessage(new { message = "set-live-logging-start-time", id = 0, startTime = startTimeMs });
    }

    /// <summary>
    /// Extract the leading timestamp from an FFXIV ACT log line ("nn|YYYY-MM-DDTHH:MM:SS...-HH:MM|...").
    /// Returns null when the field can't be parsed.
    /// </summary>
    private static DateTimeOffset? TryParseLineTime(string line)
    {
        var first = line.IndexOf('|');
        if (first < 0) return null;
        var second = line.IndexOf('|', first + 1);
        if (second < 0) return null;
        var ts = line.Substring(first + 1, second - first - 1);
        if (DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
            return dto;
        return null;
    }

    /// <summary>
    /// Start live logging — monitors the latest log file and uploads fights as they complete.
    /// </summary>
    public async Task StartLiveLogAsync(
        string logDirectory,
        int region,
        bool uploadPreviousFights,
        Func<string, FightData, int, long, long, Task> onFightComplete,
        CancellationToken cancellationToken = default)
    {
        await StartParserAsync();

        try
        {
            // Handle case where logDirectory might actually be a file path
            var actualDirectory = logDirectory;
            if (File.Exists(logDirectory))
            {
                actualDirectory = Path.GetDirectoryName(logDirectory) ?? logDirectory;
            }
            else if (!Directory.Exists(logDirectory))
            {
                var parentDir = Path.GetDirectoryName(logDirectory);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    actualDirectory = parentDir;
                }
                else
                {
                    throw new Exception($"Log directory not found: {logDirectory}");
                }
            }

            // Find the latest log file
            var files = Directory.GetFiles(actualDirectory, "*.log");
            if (files.Length == 0)
                throw new Exception("No log files found in directory");

            string logPath = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
            Plugin.Log.Information($"[LiveLog] Monitoring: {Path.GetFileName(logPath)}");

            var regionStr = ((FFLogsRegion)region).ToCode();

            int lastFightCount = 0;
            DateTime lastFileCheckTime = DateTime.UtcNow;
            bool checkPending = false;
            long lastPosition = 0;
            bool firstPass = true;
            bool fightEndDetected = false;
            DateTime fightEndDetectedTime = DateTime.MinValue;

            // Parse existing file content so the parser has context.
            var (existingLines, endPos) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, 0);
            lastPosition = endPos;

            if (existingLines.Count > 0)
            {
                // set-live-logging-start-time must use the log's own first
                // timestamp, not Date.now(). Using "now" makes the parser treat
                // all historical events as outside the live window → 0 fights.
                foreach (var line in existingLines)
                {
                    var ts = TryParseLineTime(line);
                    if (ts.HasValue)
                    {
                        var liveStartMs = ts.Value.ToUnixTimeMilliseconds();
                        SendSetLiveLoggingStartTime(liveStartMs);
                        Plugin.Log.Debug($"[LiveLog] set-live-logging-start-time: {liveStartMs} (from first log line)");
                        break;
                    }
                }

                Plugin.Log.Information($"[LiveLog] Sending {existingLines.Count} existing lines for parser context");
                await Task.Run(() => SendParseLines(existingLines, regionStr, 0), cancellationToken);
            }

            if (!uploadPreviousFights)
            {
                // pushFightIfNeeded MUST be false here: forcing a push during the
                // initial catch-up commits whatever partial buffer the parser holds
                // (e.g. post-phase debuff ticks in an ultimate), creating a phantom
                // "Unknown" fight that inflates lastFightCount and causes subsequent
                // real pulls to appear as non-new.
                var responses = SendMessageAndCollect(new
                {
                    message = "collect-fights",
                    id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    pushFightIfNeeded = false,
                    scanningOnly = false
                });

                var existingFights = FindResponseWithKey(responses, "fights");
                if (existingFights.HasValue && existingFights.Value.TryGetProperty("fights", out var fa))
                {
                    lastFightCount = fa.GetArrayLength();
                    Plugin.Log.Information($"[LiveLog] Skipping {lastFightCount} existing fight(s)");
                }

                firstPass = false;
            }

            // call-wipe must only fire for truly live lines — never for historical
            // catch-up reads. The parser already processes wipe events internally
            // during parse-lines; sending call-wipe retroactively stamps them with
            // Date.now() (real clock), corrupting fight bookkeeping. We flip this
            // flag once we've caught up with the file (first read returning 0 lines).
            bool livePhaseReady = false;

            // Main loop
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Read new lines from log file. batchStartPosition captured before
                    // the read is the pre-batch file position passed as logFilePosition.
                    long batchStartPosition = lastPosition;
                    var (newLines, newPosition) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, lastPosition);
                    lastPosition = newPosition;

                    if (newLines.Count > 0)
                    {
                        Plugin.Log.Debug($"[LiveLog] Read {newLines.Count} new lines (pos={lastPosition})");
                        checkPending = false;

                        SendParseLines(newLines, regionStr, batchStartPosition);
                        foreach (var line in newLines)
                            HandleDirectorLine(line, ref fightEndDetected, ref fightEndDetectedTime, sendCallWipe: livePhaseReady);
                    }
                    else if (!livePhaseReady)
                    {
                        livePhaseReady = true;
                        Plugin.Log.Information("[LiveLog] Caught up with file — live phase started, call-wipe now active");
                    }

                    // Check for a newer log file periodically
                    if ((DateTime.UtcNow - lastFileCheckTime).TotalSeconds > FileCheckIntervalSeconds)
                    {
                        lastFileCheckTime = DateTime.UtcNow;
                        var currentFiles = Directory.GetFiles(actualDirectory, "*.log");
                        if (currentFiles.Length > 0)
                        {
                            var newestFile = currentFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                            if (newestFile != logPath)
                            {
                                var (remainingLines, _) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, lastPosition);
                                if (remainingLines.Count > 0)
                                {
                                    Plugin.Log.Information($"[LiveLog] Reading {remainingLines.Count} remaining lines from old file before switching");
                                    SendParseLines(remainingLines, regionStr, lastPosition);
                                }

                                Plugin.Log.Information($"[LiveLog] Switching to newer log file: {Path.GetFileName(newestFile)}");
                                logPath = newestFile;
                                lastPosition = 0;
                                livePhaseReady = false;
                            }
                        }
                    }

                    // Check for fights
                    bool forceCheck = firstPass && uploadPreviousFights;
                    bool fightEndCheck = fightEndDetected && (DateTime.UtcNow - fightEndDetectedTime).TotalMilliseconds >= FightEndDelayMs;

                    if ((forceCheck || fightEndCheck) && !checkPending)
                    {
                        if (forceCheck) firstPass = false;
                        if (fightEndCheck) fightEndDetected = false;
                        checkPending = true;

                        if (forceCheck)
                            Plugin.Log.Debug($"[LiveLog] Initial read complete - checking for fights immediately");
                        else
                            Plugin.Log.Information($"[LiveLog] Fight end confirmed - pushing completed fight");

                        // pushFightIfNeeded MUST be false mid-loop: in multi-phase ultimate
                        // fights (e.g. DSR), phase-transition director codes (40000005) fire
                        // our fight-end detector repeatedly. Forcing a push at those points
                        // commits a tiny garbage buffer as a phantom "Unknown" fight.
                        // The parser commits real fights naturally — we just poll for them.
                        lastFightCount = await CheckForFightsAsync(lastFightCount, onFightComplete, pushFightIfNeeded: false);
                    }

                    await Task.Delay(LiveLogPollIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "[LiveLog] Error in loop");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            Plugin.Log.Information("[LiveLog] Stopped");

            // Final check: Upload any remaining fights
            if (engine != null)
            {
                Plugin.Log.Information("[LiveLog] Final check for remaining fights...");
                try
                {
                    await CheckForFightsAsync(lastFightCount, onFightComplete, pushFightIfNeeded: true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "[LiveLog] Error in final check");
                }
            }
        }
        finally
        {
            StopParser();
        }
    }

    /// <summary>
    /// Inspect a log line for director commands. Triggers fight-end detection for any
    /// known end code. When <paramref name="sendCallWipe"/> is true, additionally signals
    /// call-wipe on the explicit wipe code (40000011) — matches official-client behavior.
    /// </summary>
    private void HandleDirectorLine(string line, ref bool fightEndDetected, ref DateTime fightEndDetectedTime, bool sendCallWipe)
    {
        if (!line.StartsWith("33|")) return;

        if (sendCallWipe && line.Contains($"|{WipeDirectorCode}|"))
        {
            Plugin.Log.Information($"[LiveLog] Wipe director seen — sending call-wipe");
            SendCallWipe();
        }

        if (!fightEndDetected)
        {
            foreach (var code in FightEndDirectorCodes)
            {
                if (line.Contains($"|{code}|"))
                {
                    Plugin.Log.Information($"[LiveLog] Fight end detected (Director {code})");
                    fightEndDetected = true;
                    fightEndDetectedTime = DateTime.UtcNow;
                    break;
                }
            }
        }
    }

    private async Task<int> CheckForFightsAsync(int lastFightCount, Func<string, FightData, int, long, long, Task> onFightComplete, bool pushFightIfNeeded = false)
    {
        var fightsResponses = SendMessageAndCollect(new
        {
            message = "collect-fights",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            pushFightIfNeeded,
            scanningOnly = false
        });

        var fightsResult = FindResponseWithKey(fightsResponses, "fights");

        if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
        {
            var currentCount = fightsArray.GetArrayLength();

            if (currentCount > lastFightCount)
            {
                var newCount = currentCount - lastFightCount;
                Plugin.Log.Information($"[LiveLog] {newCount} NEW fight(s) detected!");

                // Conditionally pass reportCode to match what set-report-code established.
                // Before the first report is created, currentReportCode is null and we omit
                // the field entirely so the JS side sees undefined (matching expectedReportCode).
                object masterMsg = currentReportCode != null
                    ? new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reportCode = currentReportCode }
                    : (object)new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                var masterResponses = SendMessageAndCollect(masterMsg);

                var masterResult = FindResponseWithKey(masterResponses, "actorsString");

                if (masterResult.HasValue)
                {
                    var fr = fightsResult.Value;
                    long globalStartTime = fr.TryGetProperty("startTime", out var gst) ? gst.GetInt64() : 0;
                    long globalEndTime = fr.TryGetProperty("endTime", out var get) ? get.GetInt64() : globalStartTime;
                    var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
                    var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;

                    var masterStr = BuildMasterTableString(fightsResult, masterResult.Value);

                    int i = 0;
                    foreach (var fight in fightsArray.EnumerateArray())
                    {
                        if (i >= lastFightCount)
                        {
                            var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
                            if (!TryGetLastEventRelativeTime(eventsStr, out long lastRel))
                            {
                                Plugin.Log.Information($"[LiveLog] Skipping empty fight segment {i + 1} (no events)");
                                i++;
                                continue;
                            }
                            long fightEndTime = globalStartTime + lastRel;
                            var fightData = new FightData
                            {
                                Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                                StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                                EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                                EventsString = eventsStr,
                                LogVersion = logVer,
                                GameVersion = gameVer
                            };

                            Plugin.Log.Information($"[LiveLog] Uploading: {fightData.Name} (segment {i + 1})");
                            LogFightDiagnostic("LIVE", i + 1, fightData, fight);
                            await onFightComplete(masterStr, fightData, i + 1, globalStartTime, fightEndTime);
                        }
                        i++;
                    }

                    return currentCount;
                }
            }
            return currentCount > lastFightCount ? currentCount : lastFightCount;
        }
        return lastFightCount;
    }

    /// <summary>
    /// Host object exposed to JavaScript for IPC message capture.
    /// Called from JS via __ipc.capture(jsonString).
    /// </summary>
    public class IpcHost
    {
        private readonly ParserService service;

        public IpcHost(ParserService service) => this.service = service;

        public void capture(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                lock (service.ipcLock)
                {
                    service.ipcMessages.Add(doc.RootElement.Clone());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"IPC parse error: {ex.Message}");
            }
        }
    }
}
