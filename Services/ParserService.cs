using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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
    private const string ParserGameContentDetectionEnabled = "true";
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

    // Fallback parser version, used only if we can't read it out of the downloaded
    // parser bundle. The real value is detected in EnsureParserAsync so create-report
    // always advertises the same parserVersion the current parser bundle reports —
    // matching how the Archon app queries the parser via `get-parser-version` rather
    // than hardcoding it.
    private const int FallbackParserVersion = 2075;

    private readonly Plugin plugin;
    private string? parserBundlePath;
    private V8ScriptEngine? engine;
    private readonly List<JsonElement> ipcMessages = new();
    private readonly object ipcLock = new();
    private string? currentReportCode;

    /// <summary>
    /// The parser version reported by the current parser bundle (the glue's
    /// <c>const parserVersion = N</c>). Detected during <see cref="EnsureParserAsync"/>;
    /// defaults to <see cref="FallbackParserVersion"/> until then.
    /// </summary>
    public int ParserVersion { get; private set; } = FallbackParserVersion;

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
        // Cache key bumped when the upstream parser changes (parserVersion 2075).
        parserBundlePath = Path.Combine(cacheDir, "fflogs_parser_v8_v3.js");

        // Best-effort cleanup of older cache files so they don't accumulate.
        try
        {
            foreach (var stale in new[] { "fflogs_parser_v8.js", "fflogs_parser_v8_v2.js" })
            {
                var p = Path.Combine(cacheDir, stale);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }

        // If cached and fresh (< 24 hours), validate and use it
        if (File.Exists(parserBundlePath) && File.GetLastWriteTimeUtc(parserBundlePath) > DateTime.UtcNow.AddHours(-24))
        {
            var cachedSize = new FileInfo(parserBundlePath).Length;
            if (cachedSize > 10000)
            {
                Plugin.Log.Information($"Using cached parser: {parserBundlePath}");
                DetectParserVersion(await File.ReadAllTextAsync(parserBundlePath));
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
            var parserPageUrl = $"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&gameContentDetectionEnabled={ParserGameContentDetectionEnabled}&metersEnabled={ParserMetersEnabled}&liveFightDataEnabled={ParserLiveFightDataEnabled}";

            // Archon loads this page as an iframe navigation, not a fetch — match that
            // request fingerprint (navigate/iframe, no sec-ch-ua* client hints).
            string html;
            using (var pageRequest = new HttpRequestMessage(HttpMethod.Get, parserPageUrl))
            {
                FFLogsService.ApplyArchonHeaders(pageRequest, FFLogsService.RequestKind.ParserPage);
                using var pageResponse = await HttpClient.SendAsync(pageRequest);
                pageResponse.EnsureSuccessStatusCode();
                html = await pageResponse.Content.ReadAsStringAsync();
            }

            // Extract parser URL from HTML
            var match = Regex.Match(html, @"src=""([^""]+parser-ff[^""]+)""");
            if (!match.Success)
            {
                throw new Exception("Could not find parser URL in response");
            }

            var parserUrl = match.Groups[1].Value;
            Plugin.Log.Debug($"Found parser URL: {parserUrl}");

            // Download the parser JS — Archon fetches this from assets.rpglogs.com as a
            // no-cors <script> load. Match that fingerprint (no-cors/script).
            string parserCode;
            using (var scriptRequest = new HttpRequestMessage(HttpMethod.Get, parserUrl))
            {
                FFLogsService.ApplyArchonHeaders(scriptRequest, FFLogsService.RequestKind.Script);
                using var scriptResponse = await HttpClient.SendAsync(scriptRequest);
                scriptResponse.EnsureSuccessStatusCode();
                parserCode = await scriptResponse.Content.ReadAsStringAsync();
            }

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

            // Read the parser version out of the (non-minified) glue while we have it.
            DetectParserVersion(glueCode);

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
    /// Ensures the parser bundle is downloaded/cached and returns its parser version.
    /// Cheap after the first call (the bundle is cached for 24h). Call this before
    /// creating a report so create-report advertises the exact parserVersion the
    /// current parser reports, instead of a hardcoded constant.
    /// </summary>
    public async Task<int> EnsureParserVersionAsync()
    {
        await EnsureParserAsync();
        return ParserVersion;
    }

    /// <summary>
    /// Reads the parser version out of a bundle/glue string — the glue defines it as a
    /// non-minified <c>const parserVersion = N;</c>. Updates <see cref="ParserVersion"/>
    /// on success; leaves the previous (fallback) value if the marker isn't found.
    /// </summary>
    private void DetectParserVersion(string bundleOrGlue)
    {
        // The parser JS core and the glue each declare `const parserVersion = N`. When
        // passed the full bundle (parserCode + glue), the glue's value is the last match
        // because the glue is always concatenated last — take that one.
        var matches = Regex.Matches(bundleOrGlue, @"const\s+parserVersion\s*=\s*(\d{3,6})");
        if (matches.Count > 0 &&
            int.TryParse(matches[^1].Groups[1].Value, out var v) && v > 0)
        {
            if (v != ParserVersion)
                Plugin.Log.Information($"[Parser] Detected parserVersion={v} (was {ParserVersion})");
            ParserVersion = v;
        }
        else
        {
            Plugin.Log.Warning($"[Parser] Could not detect parserVersion in bundle; using {ParserVersion}");
        }
    }

    /// <summary>
    /// Browser environment shims for the FFLogs parser JavaScript.
    /// Provides mock window, document, and Web API objects that the parser expects.
    /// </summary>
    private const string BrowserShimsJs = @"
        var window = {
            location: { search: '?id=1&gameContentDetectionEnabled=true&metersEnabled=false&liveFightDataEnabled=false' },
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

        // IPC capture helper — used by both our sendToHost shim and the source.postMessage
        // mock. The parser bundle later defines its own sendToHost that mutates the data
        // object with .message/.id properties and calls event.source.postMessage(obj).
        // For Array payloads, JSON.stringify drops named properties, so we wrap them
        // explicitly into a {message, id, data} envelope before capture.
        function __captureMessage(r) {
            if (r && typeof r === 'object') {
                if (Array.isArray(r)) {
                    __ipc.capture(JSON.stringify({
                        message: r.message,
                        id: r.id,
                        data: Array.from(r)
                    }));
                    return;
                }
            }
            __ipc.capture(JSON.stringify(r));
        }

        // Our initial sendToHost (parser bundle overrides this with its own version)
        window.sendToHost = function(channel, id, event, data) {
            __captureMessage({ message: channel, id: id, data: data });
        };

        // Helper to dispatch a message to the parser (replaces stdin JSON protocol)
        function __dispatchMessage(msgJson) {
            if (!window._messageListener) throw new Error('Parser not initialized: no message listener');
            var msg = JSON.parse(msgJson);
            window._messageListener({
                data: msg,
                source: { postMessage: function(r) { __captureMessage(r); } },
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
            if (msg.ValueKind != JsonValueKind.Object) continue;
            if (msg.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out _))
                return d;
            if (msg.TryGetProperty(key, out _))
                return msg;
        }
        return null;
    }

    /// <summary>
    /// One uploadable fight: events + the slim per-fight master table that scopes only
    /// to that fight's actors/abilities. Matches the official client's per-segment upload.
    /// </summary>
    public record FightUpload(string MasterTable, FightData Fight, long FightStartTime, long FightEndTime);

    /// <summary>
    /// Process a log file using the embedded V8 parser.
    ///
    /// Mirrors the official client's two-pass flow: a scanning pass discovers raid
    /// time-windows, then each raid is re-parsed in isolation with raidsToUpload set
    /// to that single raid. The parser scopes its master table to the raid's
    /// actors/abilities, producing a slim per-fight master that matches what the
    /// official uploader sends. A single cumulative master table (the previous flow)
    /// causes FFLogs to flag the report as "outdated logger".
    /// </summary>
    public async Task<List<FightUpload>> ProcessLogAsync(string logPath, string reportCode, int region)
    {
        await StartParserAsync();

        var uploads = new List<FightUpload>();

        try
        {
            Plugin.Log.Information($"[Parser] Processing log: {Path.GetFileName(logPath)}");

            var lines = await LogFileHelper.ReadAllLinesSharedAsync(logPath);
            Plugin.Log.Debug($"[Parser] Read {lines.Length} lines");

            var regionStr = ((FFLogsRegion)region).ToCode();
            var lineList = new List<string>(lines);

            // Pull a calendar date out of the first line that has one — used by every pass.
            long? startDateMs = null;
            foreach (var line in lines)
            {
                var ts = TryParseLineTime(line);
                if (ts.HasValue)
                {
                    startDateMs = ts.Value.ToUnixTimeMilliseconds();
                    break;
                }
            }

            // ── Pass 1: scan to discover raids ─────────────────────────────────────
            SendMessage(new { message = "set-report-code", id = 0, reportCode });
            if (startDateMs.HasValue) SendSetStartDate(startDateMs.Value);

            await Task.Run(() => SendParseLines(lineList, regionStr, 0,
                scanning: true, raidsToUpload: Array.Empty<object>()));

            var scannedResponses = SendMessageAndCollect(new
            {
                message = "collect-scanned-raids",
                id = 1
            });

            var scannedRaidsElement = FindResponseByChannel(scannedResponses, "collect-scanned-raids-completed");
            if (!scannedRaidsElement.HasValue || scannedRaidsElement.Value.ValueKind != JsonValueKind.Array)
            {
                Plugin.Log.Warning("[Parser] No scanned raids in response — log may contain no fights");
                return uploads;
            }

            var rawScanned = scannedRaidsElement.Value;
            int raidCount = rawScanned.GetArrayLength();
            Plugin.Log.Information($"[Parser] Scan found {raidCount} raid(s)");

            if (raidCount == 0) return uploads;

            // Snapshot each scanned raid as a JSON string so we can pass it back to the
            // parser via raidsToUpload after clear-state — we can't hold JsonElement
            // references across SendMessageAndCollect calls because the underlying
            // ipcMessages list gets cleared.
            var scannedRaidJson = new List<string>(raidCount);
            foreach (var raid in rawScanned.EnumerateArray())
                scannedRaidJson.Add(raid.GetRawText());

            // ── Pass 2: per-raid replay with raidsToUpload filter ──────────────────
            // For each raid, clear-state, re-set report code + start date (clear-state
            // wipes them), re-parse the full file with raidsToUpload=[that_raid].
            // The parser scopes its master table to that raid's events only.
            for (int raidIdx = 0; raidIdx < scannedRaidJson.Count; raidIdx++)
            {
                Plugin.Log.Debug($"[Parser] Replaying raid {raidIdx + 1}/{scannedRaidJson.Count}");

                SendMessageAndCollect(new { message = "clear-state", id = 100 + raidIdx });
                SendMessage(new { message = "set-report-code", id = 0, reportCode });
                if (startDateMs.HasValue) SendSetStartDate(startDateMs.Value);

                // raidsToUpload is a single-element JSON array containing the scanned raid
                // verbatim — the parser only reads .start and .end for time-window filtering.
                var raidsToUploadJson = $"[{scannedRaidJson[raidIdx]}]";

                await Task.Run(() => SendMessage(new
                {
                    message = "parse-lines",
                    id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    lines = lineList,
                    scanning = false,
                    selectedRegion = regionStr,
                    raidsToUpload = JsonDocument.Parse(raidsToUploadJson).RootElement.Clone(),
                    logFilePosition = 0L
                }));

                var fightsResponses = SendMessageAndCollect(new
                {
                    message = "collect-fights",
                    id = 200 + raidIdx,
                    pushFightIfNeeded = false,
                    scanningOnly = false
                });

                var fightsResult = FindResponseWithKey(fightsResponses, "fights");
                if (!fightsResult.HasValue ||
                    !fightsResult.Value.TryGetProperty("fights", out var fightsArray) ||
                    fightsArray.GetArrayLength() == 0)
                {
                    Plugin.Log.Warning($"[Parser] Raid {raidIdx + 1}: no fights produced after replay");
                    continue;
                }

                var masterResponses = SendMessageAndCollect(new
                {
                    message = "collect-master-info",
                    id = 300 + raidIdx,
                    reportCode
                });

                var masterResult = FindResponseWithKey(masterResponses, "actorsString");
                if (!masterResult.HasValue)
                {
                    Plugin.Log.Warning($"[Parser] Raid {raidIdx + 1}: no master info");
                    continue;
                }

                var fr = fightsResult.Value;
                long globalStartTime = fr.TryGetProperty("startTime", out var st) ? st.GetInt64() : 0;
                long globalEndTime = fr.TryGetProperty("endTime", out var et) ? et.GetInt64() : globalStartTime;
                var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
                var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;

                var perFightMaster = BuildMasterTableString(fightsResult, masterResult.Value);

                // A scanned raid may produce multiple committed fights (e.g. multiple
                // wipes in a pull). Emit each as its own segment so the upload mirrors
                // what the official client sends.
                int fightIdxInRaid = 0;
                foreach (var fight in fightsArray.EnumerateArray())
                {
                    var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
                    if (!TryGetLastEventRelativeTime(eventsStr, out long lastRel))
                    {
                        fightIdxInRaid++;
                        continue;
                    }
                    long fightEndTime = globalStartTime + lastRel;
                    var fd = new FightData
                    {
                        Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                        StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                        EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                        EventsString = eventsStr,
                        EventCount = fight.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number
                            ? ec.GetInt32() : 0,
                        LogVersion = logVer,
                        GameVersion = gameVer
                    };
                    LogFightDiagnostic("BULK", uploads.Count + 1, fd, fight);
                    uploads.Add(new FightUpload(perFightMaster, fd, globalStartTime, fightEndTime));
                    fightIdxInRaid++;
                }
            }

            Plugin.Log.Information($"[Parser] Prepared {uploads.Count} fight upload(s)");
        }
        finally
        {
            StopParser();
        }

        return uploads;
    }

    /// <summary>
    /// Builds the master table string that FFLogs expects for report uploads.
    ///
    /// Matches the official client's compressMasterInfo format:
    ///   Line 1: "{logVersion}|{gameVersion}|{logFileDetails}"
    ///   Then in strict order, each section emitted as:
    ///     - Line: {lastAssigned*ID} (from master data)
    ///     - Raw: {sectionString} (already newline-delimited)
    ///   Sections: actors, abilities, tuples, pets.
    /// </summary>
    private string BuildMasterTableString(JsonElement? fightsData, JsonElement masterData)
    {
        var sb = new StringBuilder();

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

        sb.Append(logVersion).Append('|').Append(gameVersion).Append('|').Append(logFileDetails).Append('\n');

        // Section order and the lastAssigned*ID key paired with each string field.
        var sections = new (string IdKey, string StringKey)[]
        {
            ("lastAssignedActorID", "actorsString"),
            ("lastAssignedAbilityID", "abilitiesString"),
            ("lastAssignedTupleID", "tuplesString"),
            ("lastAssignedPetID", "petsString"),
        };

        foreach (var (idKey, stringKey) in sections)
        {
            int lastId = masterData.TryGetProperty(idKey, out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : 0;
            var value = masterData.TryGetProperty(stringKey, out var prop) ? prop.GetString() ?? "" : "";

            sb.Append(lastId).Append('\n').Append(value);
            // Official sectionString already ends with \n per entry; only add one if missing
            // so the next lastAssigned line starts on its own row.
            if (value.Length > 0 && value[^1] != '\n')
                sb.Append('\n');
        }

        var result = sb.ToString();
        Plugin.Log.Debug($"[Parser] Master table: {result.Length} chars");
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
        SendParseLines(lines, regionStr, position, scanning: false, raidsToUpload: Array.Empty<object>());
    }

    private void SendParseLines(List<string> lines, string regionStr, long position, bool scanning, object raidsToUpload)
    {
        SendMessage(new
        {
            message = "parse-lines",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            lines,
            scanning,
            selectedRegion = regionStr,
            raidsToUpload,
            logFilePosition = position
        });
    }

    /// <summary>
    /// Find the first IPC response with the given message name (e.g. "collect-scanned-raids-completed").
    /// Use this when the response payload is an array or has no distinguishing key
    /// — <see cref="FindResponseWithKey"/> only matches object responses by property name.
    /// All parser responses use the `message` field for the channel name; array payloads
    /// are wrapped by __captureMessage so they expose `message`/`id`/`data` consistently.
    /// </summary>
    private static JsonElement? FindResponseByChannel(List<JsonElement> responses, string channel)
    {
        foreach (var msg in responses)
        {
            if (msg.ValueKind != JsonValueKind.Object) continue;
            if (msg.TryGetProperty("message", out var c) && c.ValueKind == JsonValueKind.String &&
                c.GetString() == channel)
            {
                return msg.TryGetProperty("data", out var d) ? d : msg;
            }
        }
        return null;
    }

    private void SendCallWipe()
    {
        SendMessage(new { message = "call-wipe", id = 0 });
    }

    /// <summary>
    /// Sends check-dungeon-inactivity and returns the parser's <c>justFired</c> flag —
    /// true when the parser has just decided the current dungeon/game content went
    /// inactive and ended it (so its fight is now committable). <paramref name="clientSideTimeMs"/>
    /// is wall-clock time in ms (what the Archon app passes as <c>Date.now()</c>).
    /// </summary>
    private bool SendCheckDungeonInactivity(long clientSideTimeMs)
    {
        var responses = SendMessageAndCollect(new
        {
            message = "check-dungeon-inactivity",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            clientSideTime = clientSideTimeMs
        });

        var result = FindResponseByChannel(responses, "check-dungeon-inactivity-completed");
        return result.HasValue
            && result.Value.ValueKind == JsonValueKind.Object
            && result.Value.TryGetProperty("justFired", out var jf)
            && jf.ValueKind == JsonValueKind.True;
    }

    private void SendSetLiveLoggingStartTime(long startTimeMs)
    {
        SendMessage(new { message = "set-live-logging-start-time", id = 0, startTime = startTimeMs });
    }

    private void SendSetStartDate(long startDateMs)
    {
        SendMessage(new { message = "set-start-date", id = 0, startDate = startDateMs });
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
    /// Peek at the tail of the log file to find the most recent parseable timestamp,
    /// without feeding the file content to the parser. Used to anchor the parser's
    /// live window to the log's own clock — events that arrive after this point are
    /// considered live. For real-time ACT logs the tail timestamp ≈ now; for replays
    /// it's the original recording time, which keeps the live-window filter consistent
    /// with the timestamps that will actually arrive.
    /// </summary>
    private static long? TryReadLatestTimestampFromFile(string logPath)
    {
        try
        {
            using var fs = File.Open(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            if (len == 0) return null;

            int peekSize = (int)Math.Min(8192, len);
            fs.Seek(-peekSize, SeekOrigin.End);
            var buf = new byte[peekSize];
            int read = fs.Read(buf, 0, peekSize);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            // Walk lines from the back; first parseable timestamp is the most recent.
            var lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var ts = TryParseLineTime(lines[i]);
                if (ts.HasValue) return ts.Value.ToUnixTimeMilliseconds();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Start live logging — monitors the latest log file and uploads fights as they complete.
    /// The <paramref name="onFightComplete"/> callback receives
    /// (masterTable, fight, segmentId, startTime, endTime, inProgressEventCount); a non-zero
    /// inProgressEventCount marks a still-in-progress fight (real-time live logging).
    /// </summary>
    public async Task StartLiveLogAsync(
        string logDirectory,
        int region,
        bool uploadPreviousFights,
        Func<string, FightData, int, long, long, int, Task> onFightComplete,
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
            // Real-time live logging: eventCount of the in-progress fight last uploaded as a
            // provisional segment. Reset to -1 whenever a fight commits so the next fight
            // starts fresh. Stays -1 (unused) unless the account has realTimeLiveLogging.
            int lastInProgressEventCount = -1;

            if (uploadPreviousFights)
            {
                // Bulk-upload-via-live-mode: feed the entire existing log so the parser
                // can detect and upload every fight in the file.
                var (existingLines, endPos) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, 0);
                lastPosition = endPos;

                if (existingLines.Count > 0)
                {
                    foreach (var line in existingLines)
                    {
                        var ts = TryParseLineTime(line);
                        if (ts.HasValue)
                        {
                            var liveStartMs = ts.Value.ToUnixTimeMilliseconds();
                            SendSetStartDate(liveStartMs);
                            SendSetLiveLoggingStartTime(liveStartMs);
                            Plugin.Log.Debug($"[LiveLog] set-start-date + set-live-logging-start-time: {liveStartMs} (from first log line)");
                            break;
                        }
                    }

                    Plugin.Log.Information($"[LiveLog] Sending {existingLines.Count} existing lines for parser context");
                    await Task.Run(() => SendParseLines(existingLines, regionStr, 0), cancellationToken);
                }
            }
            else
            {
                // Feed historical content with scanning=true so the parser learns
                // zone/encounter context (e.g. that the player is currently inside
                // Dragonsong's Reprise) without committing fights or adding actors
                // to the master table. Without this context, ultimates fragment into
                // one trash-fight commit per phase boundary because the parser doesn't
                // know the surrounding events are part of a recognized encounter.
                //
                // A previous attempt tailed straight from EOF to keep the master table
                // clean, but that path also discarded the zone-in event the parser
                // needs to identify the encounter — so DSR/UWU/UCOB pulls uploaded
                // as separate trash kills per phase. Scanning mode gives us the best
                // of both: zone/raid metadata accumulates, per-actor master state
                // does not.
                var (existingLines, endPos) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, 0);
                lastPosition = endPos;

                long? firstLineMs = null;
                foreach (var line in existingLines)
                {
                    var ts = TryParseLineTime(line);
                    if (ts.HasValue) { firstLineMs = ts.Value.ToUnixTimeMilliseconds(); break; }
                }
                if (firstLineMs.HasValue) SendSetStartDate(firstLineMs.Value);

                // Anchor the live window to the log's own clock so events that arrive
                // after this point qualify as live. For a real-time ACT log the tail
                // timestamp ≈ now; for a replayed log it's the original recording time,
                // which keeps the live filter aligned with the timestamps that will
                // actually arrive.
                var anchorMs = TryReadLatestTimestampFromFile(logPath)
                              ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SendSetLiveLoggingStartTime(anchorMs);

                if (existingLines.Count > 0)
                {
                    Plugin.Log.Information($"[LiveLog] Scanning {existingLines.Count} historical line(s) for context (no commits)");
                    await Task.Run(() => SendParseLines(existingLines, regionStr, 0,
                        scanning: true, raidsToUpload: Array.Empty<object>()), cancellationToken);
                }
                Plugin.Log.Information($"[LiveLog] Live tail starts at pos={lastPosition}; live-window anchor={anchorMs}");
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

                    // Ask the parser whether dungeon/game content has gone inactive
                    // ("slow growth"). Archon calls this every live poll with wall-clock
                    // time; on justFired the parser has ended the dungeon, so its fight
                    // commits and we poll for it. This replaces guessing dungeon ends from
                    // a hardcoded director-code list. Only meaningful once we're tailing
                    // live — during historical catch-up the wall-clock window doesn't line
                    // up with the log's own timestamps, so we skip it until livePhaseReady.
                    if (livePhaseReady && !fightEndDetected)
                    {
                        bool justFired = false;
                        try
                        {
                            justFired = SendCheckDungeonInactivity(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Debug($"[LiveLog] check-dungeon-inactivity failed: {ex.Message}");
                        }
                        if (justFired)
                        {
                            Plugin.Log.Information("[LiveLog] Parser reported dungeon inactivity — treating as fight end");
                            fightEndDetected = true;
                            fightEndDetectedTime = DateTime.UtcNow;
                        }
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
                        int prevFightCount = lastFightCount;
                        lastFightCount = await CheckForFightsAsync(lastFightCount, onFightComplete, pushFightIfNeeded: false);
                        // A fight just committed — the next in-progress fight starts fresh.
                        if (lastFightCount != prevFightCount) lastInProgressEventCount = -1;
                    }

                    // Real-time live logging only: push the currently in-progress fight as a
                    // provisional segment so the report updates mid-fight (Archon's
                    // collect-in-progress-fight path). Inert for accounts without the feature.
                    if (plugin.FFLogsService.RealTimeLiveLoggingEnabled && livePhaseReady && !checkPending)
                    {
                        try
                        {
                            lastInProgressEventCount = await CheckInProgressFightAsync(
                                lastFightCount, lastInProgressEventCount, onFightComplete);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Debug($"[LiveLog] in-progress upload failed: {ex.Message}");
                        }
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

    private async Task<int> CheckForFightsAsync(int lastFightCount, Func<string, FightData, int, long, long, int, Task> onFightComplete, bool pushFightIfNeeded = false)
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
                                EventCount = fight.TryGetProperty("eventCount", out var ec) && ec.ValueKind == JsonValueKind.Number
                                    ? ec.GetInt32() : 0,
                                LogVersion = logVer,
                                GameVersion = gameVer
                            };

                            Plugin.Log.Information($"[LiveLog] Uploading: {fightData.Name} (segment {i + 1})");
                            LogFightDiagnostic("LIVE", i + 1, fightData, fight);
                            // Committed fight — inProgressEventCount 0 (finalized).
                            await onFightComplete(masterStr, fightData, i + 1, globalStartTime, fightEndTime, 0);
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
    /// Real-time live logging only: uploads the currently in-progress fight as a
    /// provisional segment (segmentId = committedFightCount + 1) with a non-zero
    /// inProgressEventCount, mirroring the Archon app's <c>collect-in-progress-fight</c>
    /// usage. Re-uploads only when the fight has grown (eventCount increased). Returns the
    /// eventCount last uploaded, or <paramref name="lastInProgressEventCount"/> if nothing
    /// was uploaded. The fight is finalized normally by <see cref="CheckForFightsAsync"/>
    /// once it commits (same segmentId, inProgressEventCount 0).
    /// </summary>
    private async Task<int> CheckInProgressFightAsync(int committedFightCount, int lastInProgressEventCount,
        Func<string, FightData, int, long, long, int, Task> onFightComplete)
    {
        var responses = SendMessageAndCollect(new
        {
            message = "collect-in-progress-fight",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        var fightsResult = FindResponseWithKey(responses, "fights");
        if (!fightsResult.HasValue ||
            !fightsResult.Value.TryGetProperty("fights", out var fightsArray) ||
            fightsArray.GetArrayLength() == 0)
            return lastInProgressEventCount; // no fight in progress

        // There is only ever a single in-progress fight.
        JsonElement fight = default;
        bool hasFight = false;
        foreach (var f in fightsArray.EnumerateArray()) { fight = f; hasFight = true; break; }
        if (!hasFight) return lastInProgressEventCount;

        var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
        if (!TryGetLastEventRelativeTime(eventsStr, out long lastRel))
            return lastInProgressEventCount; // nothing recorded yet

        int eventCount = fight.TryGetProperty("eventCount", out var ecp) && ecp.ValueKind == JsonValueKind.Number
            ? ecp.GetInt32() : 0;
        if (eventCount <= lastInProgressEventCount)
            return lastInProgressEventCount; // no growth since last provisional upload

        object masterMsg = currentReportCode != null
            ? new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reportCode = currentReportCode }
            : (object)new { message = "collect-master-info", id = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var masterResponses = SendMessageAndCollect(masterMsg);
        var masterResult = FindResponseWithKey(masterResponses, "actorsString");
        if (!masterResult.HasValue)
            return lastInProgressEventCount;

        var fr = fightsResult.Value;
        long globalStartTime = fr.TryGetProperty("startTime", out var gst) ? gst.GetInt64() : 0;
        var logVer = fr.TryGetProperty("logVersion", out var lvProp) ? lvProp.GetInt32() : 72;
        var gameVer = fr.TryGetProperty("gameVersion", out var gvProp) ? gvProp.GetInt32() : 1;
        var masterStr = BuildMasterTableString(fightsResult, masterResult.Value);

        var fightData = new FightData
        {
            Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
            StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
            EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
            EventsString = eventsStr,
            EventCount = eventCount,
            LogVersion = logVer,
            GameVersion = gameVer
        };
        long fightEndTime = globalStartTime + lastRel;
        int segmentId = committedFightCount + 1;

        Plugin.Log.Information($"[LiveLog] Uploading IN-PROGRESS {fightData.Name} (segment {segmentId}, inProgressEventCount={eventCount})");
        await onFightComplete(masterStr, fightData, segmentId, globalStartTime, fightEndTime, eventCount);
        return eventCount;
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
