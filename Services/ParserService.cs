using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFLogsPlugin.Helpers;
using FFLogsPlugin.Models;

namespace FFLogsPlugin.Services;

/// <summary>
/// Manages the FFLogs parser by calling Node.js subprocess directly.
/// </summary>
public class ParserService : IDisposable
{
    private const string FFLOGS_URL = "https://www.fflogs.com";

    // Configurable timing constants (formerly hardcoded magic numbers)
    private const int LiveLogPollIntervalMs = 500;
    private const int FightEndDelayMs = 5000;
    private const int FileCheckIntervalSeconds = 10;

    // Parser page query parameters — exposed as constants for future configurability
    private const string ParserMetersEnabled = "false";
    private const string ParserLiveFightDataEnabled = "false";

    /// <summary>
    /// Director command codes that indicate a fight has ended.
    /// These are FFXIV network opcodes from log line type 33 (NetworkDirector).
    /// </summary>
    private static readonly HashSet<string> FightEndDirectorCodes = new()
    {
        "40000011", // Wipe cleanup — entities cleared after a wipe
        "40000002", // Victory / duty complete
        "40000003", // Duty complete (alternative)
        "40000005", // Duty complete (alternative)
    };

    private readonly Plugin plugin;
    private string? parserBundlePath;
    private Process? parserProcess;
    private int sendCounter = 0; // Instance field, not static — each service instance tracks its own counter

    // Channel-based IPC: messages arrive from the output reader and are consumed by WaitForMessageAsync
    private Channel<JsonElement>? ipcChannel;
    // Buffer for messages that didn't match the current wait predicate (put-back)
    // Capped to prevent unbounded growth during long live-logging sessions with repeated timeouts
    private const int MaxBufferSize = 500;
    private readonly List<JsonElement> ipcBuffer = new();
    private readonly object bufferLock = new();

    // Use the authenticated HttpClient from FFLogsService
    private HttpClient HttpClient => plugin.FFLogsService.HttpClient;

    public ParserService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Dispose()
    {
        StopParser();
        GC.SuppressFinalize(this);
    }

    ~ParserService()
    {
        // Guarantee cleanup of orphaned Node processes even if Dispose wasn't called
        StopParser();
    }

    private void StopParser()
    {
        if (parserProcess != null && !parserProcess.HasExited)
        {
            try { parserProcess.Kill(); } catch { }
            parserProcess.Dispose();
            parserProcess = null;
        }
    }

    /// <summary>
    /// Ensures the parser bundle is downloaded, validated, and cached.
    /// </summary>
    private async Task EnsureParserAsync()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "FFLogsPlugin"
        );
        Directory.CreateDirectory(cacheDir);
        parserBundlePath = Path.Combine(cacheDir, "fflogs_parser_bundle.js");

        // If cached and fresh (< 24 hours), validate and use it
        if (File.Exists(parserBundlePath) && File.GetLastWriteTimeUtc(parserBundlePath) > DateTime.UtcNow.AddHours(-24))
        {
            // Validate cached bundle isn't corrupted (e.g. disk full during previous write)
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
        
        // Download to a temp file first so we don't cache partial/invalid downloads
        var tempPath = parserBundlePath + ".tmp";
        
        try
        {
            // Fetch the parser page
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var parserPageUrl = $"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&metersEnabled={ParserMetersEnabled}&liveFightDataEnabled={ParserLiveFightDataEnabled}";
            
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
            // Find all inline <script type="text/javascript"> blocks,
            // then pick the one that contains the ipcCollectFights function.
            // Using per-block matching avoids crossing </script>...<script> boundaries
            // which would embed raw HTML tags into the JS bundle.
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
            
            // Create runner wrapper
            var runnerCode = @"
const fs = require('fs');
const readline = require('readline');

// Mock browser environment
const window = {
    location: { search: '?id=1&metersEnabled=false&liveFightDataEnabled=false' },
    addEventListener: (type, listener) => { if (type === 'message') global.messageListener = listener; },
    removeEventListener: () => {},
    dispatchEvent: () => true
};
const document = {
    createElement: () => ({ style: {}, appendChild: () => {}, addEventListener: () => {} }),
    getElementsByTagName: () => [{ appendChild: () => {} }],
    getElementById: () => null,
    querySelector: () => null,
    querySelectorAll: () => [],
    body: { appendChild: () => {} },
    head: { appendChild: () => {} },
    readyState: 'complete',
    addEventListener: () => {},
    removeEventListener: () => {}
};
global.window = window;
global.document = document;
global.self = window;
global.navigator = { userAgent: 'FFLogsPlugin/1.0', platform: 'Win32' };
global.location = window.location;
global.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
global.sessionStorage = global.localStorage;
global.setTimeout = (fn) => { fn(); return 0; };
global.setInterval = () => 0;
global.clearTimeout = () => {};
global.clearInterval = () => {};
global.performance = { now: () => Date.now() };
global.fetch = () => Promise.reject('fetch not supported');
global.URLSearchParams = class {
    constructor(init) { this._params = {}; }
    get(k) { return this._params[k]; }
    set(k, v) { this._params[k] = v; }
};
global.URL = class {
    constructor(url) { this.href = url; this.searchParams = new URLSearchParams(); }
};
global.TextEncoder = class { encode(s) { return Buffer.from(s); } };
global.TextDecoder = class { decode(b) { return b.toString(); } };

// IPC output
window.sendToHost = (channel, id, event, data) => {
    console.log('__IPC__:' + JSON.stringify({ channel, id, data }));
};

// Readline for input
const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: false });
rl.on('line', (line) => {
    if (!line.trim()) return;
    try {
        const msg = JSON.parse(line);
        if (global.messageListener) {
            global.messageListener({
                data: msg,
                source: { postMessage: (r) => console.log('__IPC__:' + JSON.stringify(r)) },
                origin: 'emulator'
            });
        }
    } catch (e) {
        console.error('Parse error:', e.message);
    }
});

console.log('__IPC__:' + JSON.stringify({ channel: 'ready', data: {} }));

";

            // Bundle everything
            var fullBundle = runnerCode + "\n\n" + parserCode + "\n\n" + glueCode;
            
            // Validate that the bundle looks like valid parser JS
            if (fullBundle.Length < 10000 || !fullBundle.Contains("ipcCollectFights"))
            {
                throw new Exception($"Downloaded parser bundle appears invalid (size={fullBundle.Length}, missing expected markers). " +
                                    "This may be a partial download or a Cloudflare error page.");
            }

            // Detect HTML contamination — raw script/HTML tags should never appear in valid JS
            if (fullBundle.Contains("<script") || fullBundle.Contains("</script>"))
            {
                throw new Exception("Parser bundle contains raw HTML tags — " +
                    "the FFLogs parser page structure may have changed. " +
                    "Please report this issue.");
            }
            
            // Write to temp file, then rename — atomic-ish to avoid caching partial downloads
            await File.WriteAllTextAsync(tempPath, fullBundle);
            
            if (File.Exists(parserBundlePath))
                File.Delete(parserBundlePath);
            File.Move(tempPath, parserBundlePath);
            
            Plugin.Log.Information($"Parser downloaded and cached ({fullBundle.Length} bytes)");
        }
        catch (Exception ex)
        {
            // Clean up temp file on failure
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Plugin.Log.Error(ex, "Failed to download parser");
            throw new Exception($"Failed to download parser: {ex.Message}");
        }
    }

    private async Task StartParserProcessAsync()
    {
        await EnsureParserAsync();

        // Kill any existing process to start fresh
        if (parserProcess != null)
        {
            try
            {
                if (!parserProcess.HasExited)
                {
                    parserProcess.Kill();
                    parserProcess.WaitForExit(1000);
                }
                parserProcess.Dispose();
            }
            catch { }
            parserProcess = null;
        }

        // Check for bundled node.exe in plugin directory first
        var pluginDir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
        var bundledNode = pluginDir != null ? Path.Combine(pluginDir, "node.exe") : null;
        var nodeExecutable = (bundledNode != null && File.Exists(bundledNode)) ? bundledNode : "node";
        
        if (bundledNode != null && File.Exists(bundledNode))
        {
            Plugin.Log.Information($"Using bundled Node.js: {bundledNode}");
        }
        else
        {
            Plugin.Log.Information("Using system Node.js");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExecutable,
            Arguments = $"\"{parserBundlePath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(parserBundlePath)!
        };

        parserProcess = new Process { StartInfo = startInfo };
        
        // Create a fresh channel for this process
        ipcChannel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        lock (bufferLock) ipcBuffer.Clear();

        parserProcess.OutputDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            if (args.Data.StartsWith("__IPC__:"))
            {
                try
                {
                    var json = args.Data.Substring(8);
                    var doc = JsonDocument.Parse(json);
                    ipcChannel?.Writer.TryWrite(doc.RootElement.Clone());
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug($"IPC parse error: {ex.Message}");
                }
            }
            else
            {
                Plugin.Log.Debug($"[Parser] {args.Data}");
            }
        };

        parserProcess.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
                Plugin.Log.Warning($"[Parser stderr] {args.Data}");
        };

        parserProcess.Start();
        parserProcess.BeginOutputReadLine();
        parserProcess.BeginErrorReadLine();

        // Wait for ready signal
        var readyMsg = await WaitForMessageAsync(
            e => e.TryGetProperty("channel", out var c) && c.GetString() == "ready",
            timeoutMs: 10000);

        if (readyMsg == null)
            throw new Exception("Parser process did not start in time");

        Plugin.Log.Information("Parser process ready");
    }

    private async Task SendMessageAsync(object message)
    {
        if (parserProcess == null || parserProcess.HasExited)
            throw new InvalidOperationException("Parser process is not running");
        
        var seq = Interlocked.Increment(ref sendCounter);
        var json = JsonSerializer.Serialize(message);
        await parserProcess.StandardInput.WriteLineAsync(json);
        await parserProcess.StandardInput.FlushAsync();
        Plugin.Log.Debug($"[Parser] Sent #{seq}: {json.Substring(0, Math.Min(200, json.Length))}...");
    }

    /// <summary>
    /// Unified message waiting method. Reads from the IPC channel until a message
    /// matching the predicate is found, or the timeout expires.
    /// Non-matching messages are buffered for future waits.
    /// Optionally transforms the matched message before returning.
    /// </summary>
    private async Task<JsonElement?> WaitForMessageAsync(
        Func<JsonElement, bool> predicate,
        Func<JsonElement, JsonElement>? transform = null,
        int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        if (ipcChannel == null) return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        // First check the buffer for an already-received matching message
        lock (bufferLock)
        {
            for (int i = 0; i < ipcBuffer.Count; i++)
            {
                if (predicate(ipcBuffer[i]))
                {
                    var match = ipcBuffer[i];
                    ipcBuffer.RemoveAt(i);
                    return transform != null ? transform(match) : match;
                }
            }
        }

        // Read from the channel until we find a match or timeout
        try
        {
            while (await ipcChannel.Reader.WaitToReadAsync(timeoutCts.Token))
            {
                while (ipcChannel.Reader.TryRead(out var item))
                {
                    if (predicate(item))
                    {
                        return transform != null ? transform(item) : item;
                    }
                    
                    // Not a match — buffer it for future waits (with cap to prevent unbounded growth)
                    lock (bufferLock)
                    {
                        if (ipcBuffer.Count >= MaxBufferSize)
                        {
                            ipcBuffer.RemoveAt(0); // Evict oldest
                        }
                        ipcBuffer.Add(item);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        return null;
    }

    /// <summary>
    /// Wait for a message on a specific IPC channel name (e.g. "ready").
    /// </summary>
    private Task<JsonElement?> WaitForChannelAsync(string channel, int timeoutMs = 10000, CancellationToken ct = default)
    {
        return WaitForMessageAsync(
            e => e.TryGetProperty("channel", out var c) && c.GetString() == channel,
            timeoutMs: timeoutMs,
            ct: ct);
    }

    /// <summary>
    /// Wait for a message containing a specific key in its data payload.
    /// Checks both wrapped format (data.key) and direct format (key).
    /// Returns the unwrapped data element containing the key.
    /// </summary>
    private Task<JsonElement?> WaitForKeyAsync(string key, int timeoutMs = 10000, CancellationToken ct = default)
    {
        // Capture the resolved element during predicate evaluation to avoid redundant lookups in the transform
        JsonElement resolvedElement = default;

        return WaitForMessageAsync(
            e =>
            {
                if (e.TryGetProperty("data", out var d) && d.TryGetProperty(key, out _))
                {
                    resolvedElement = d;
                    return true;
                }
                if (e.TryGetProperty(key, out _))
                {
                    resolvedElement = e;
                    return true;
                }
                return false;
            },
            transform: _ => resolvedElement,
            timeoutMs: timeoutMs,
            ct: ct);
    }

    /// <summary>
    /// Process a log file using the Node.js parser.
    /// </summary>
    public async Task<(string masterData, List<FightData> fights, long startTime, long endTime)> ProcessLogAsync(string logPath, string reportCode, int region)
    {
        await StartParserProcessAsync();

        var fights = new List<FightData>();
        long startTime = 0;
        long endTime = 0;
        string masterStr = "";

        try
        {
            Plugin.Log.Information($"[Parser] Processing log: {Path.GetFileName(logPath)}");

            // Read log file with sharing enabled (ACT may have it open)
            var lines = await LogFileHelper.ReadAllLinesSharedAsync(logPath);
            Plugin.Log.Debug($"[Parser] Read {lines.Length} lines");

            var regionStr = ((FFLogsRegion)region).ToCode();

            // Set report code
            await SendMessageAsync(new { message = "set-report-code", id = 0, reportCode });
            await Task.Delay(100);

            // Parse lines
            await SendMessageAsync(new
            {
                message = "parse-lines",
                id = 1,
                lines = lines,
                scanning = false,
                selectedRegion = regionStr,
                raidsToUpload = Array.Empty<int>()
            });

            // Wait for parsing to complete (longer for large files)
            await Task.Delay(Math.Max(500, lines.Length / 100));

            // Collect fights
            await SendMessageAsync(new
            {
                message = "collect-fights",
                id = 2,
                pushFightIfNeeded = false,
                scanningOnly = false
            });

            // Wait for fights data
            var fightsResult = await WaitForKeyAsync("fights", 15000);
            
            if (fightsResult.HasValue)
            {
                Plugin.Log.Debug($"[Parser] Got fights result with {(fightsResult.Value.TryGetProperty("fights", out var f) ? f.GetArrayLength() : 0)} fights");
            }
            else
            {
                Plugin.Log.Warning("[Parser] No fights result received!");
                lock (bufferLock)
                {
                    Plugin.Log.Debug($"[Parser] Buffer has {ipcBuffer.Count} messages");
                    foreach (var msg in ipcBuffer)
                    {
                        var keys = new List<string>();
                        foreach (var prop in msg.EnumerateObject())
                            keys.Add(prop.Name);
                        Plugin.Log.Debug($"[Parser] Message keys: {string.Join(", ", keys)}");
                    }
                }
            }
            
            // Collect master info
            await SendMessageAsync(new
            {
                message = "collect-master-info",
                id = 3,
                reportCode
            });

            // Wait for master data
            var masterResult = await WaitForKeyAsync("actorsString", 15000);

            // Extract fights array — WaitForKeyAsync already unwraps the data envelope,
            // so fightsResult directly contains the element with "fights" as a property.
            if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
            {
                foreach (var fight in fightsArray.EnumerateArray())
                {
                    fights.Add(new FightData
                    {
                        Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                        StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                        EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                        EventsString = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : ""
                    });
                }

                // Global timestamps live on the same unwrapped element
                var fr = fightsResult.Value;
                startTime = fr.TryGetProperty("startTime", out var st) ? st.GetInt64() : 0;
                endTime = fr.TryGetProperty("endTime", out var et) ? et.GetInt64() : 0;
            }

            Plugin.Log.Information($"[Parser] Extracted {fights.Count} fights");
            if (masterResult.HasValue)
            {
                JsonElement? fightsHeader = fightsResult;
                masterStr = BuildMasterTableString(fightsHeader, masterResult.Value);
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
    ///   Line 1: "{logVersion}|{gameVersion}|{logFileDetails}"    — header from fights data
    ///   Then for each section (in strict order):
    ///     - Line: count of entries
    ///     - Lines: the entries themselves
    /// 
    /// Section order must match the official FFLogs client:
    ///   1. actorsString   — combat actors (players, enemies, NPCs) with IDs and names
    ///   2. abilitiesString — all abilities/spells seen in the log with IDs and names
    ///   3. tuplesString   — actor-ability tuples linking who used what
    ///   4. petsString     — pet/companion actors and their owners
    /// </summary>
    private string BuildMasterTableString(JsonElement? fightsData, JsonElement masterData)
    {
        var parts = new List<string>();

        // Header info comes from fights data
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

        // Header line
        parts.Add($"{logVersion}|{gameVersion}|{logFileDetails}");

        // Sections in strict order (must match official FFLogs client — see summary above)
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
    /// Start live logging — monitors the latest log file and uploads fights as they complete.
    /// </summary>
    public async Task StartLiveLogAsync(
        string logDirectory,
        int region,
        bool uploadPreviousFights,
        Func<string, FightData, int, long, long, Task> onFightComplete,
        CancellationToken cancellationToken = default)
    {
        await StartParserProcessAsync();

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

            var logPath = files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
            Plugin.Log.Information($"[LiveLog] Monitoring: {Path.GetFileName(logPath)}");

            var regionStr = ((FFLogsRegion)region).ToCode();

            int lastFightCount = 0;
            DateTime lastActivityTime = DateTime.UtcNow;
            DateTime lastFileCheckTime = DateTime.UtcNow;
            bool checkPending = false;
            long lastPosition = 0;
            bool firstPass = true;
            bool fightEndDetected = false;
            DateTime fightEndDetectedTime = DateTime.MinValue;

            // Always parse existing file content so the parser has context
            {
                var (existingLines, endPos) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, 0);
                lastPosition = endPos;

                if (existingLines.Count > 0)
                {
                    Plugin.Log.Information($"[LiveLog] Sending {existingLines.Count} existing lines for parser context");
                    await SendMessageAsync(new
                    {
                        message = "parse-lines",
                        id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        lines = existingLines,
                        scanning = false,
                        selectedRegion = regionStr,
                        raidsToUpload = Array.Empty<object>()
                    });

                    await Task.Delay(Math.Max(500, existingLines.Count / 100), cancellationToken);
                }

                if (!uploadPreviousFights)
                {
                    await SendMessageAsync(new
                    {
                        message = "collect-fights",
                        id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        pushFightIfNeeded = true,
                        scanningOnly = false
                    });

                    var existingFights = await WaitForKeyAsync("fights", 5000, cancellationToken);
                    if (existingFights.HasValue && existingFights.Value.TryGetProperty("fights", out var fa))
                    {
                        lastFightCount = fa.GetArrayLength();
                        Plugin.Log.Information($"[LiveLog] Skipping {lastFightCount} existing fight(s)");
                    }

                    firstPass = false;
                }
            }

            // Main loop
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Read new lines from log file
                    var (newLines, newPosition) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, lastPosition);
                    lastPosition = newPosition;

                    if (newLines.Count > 0)
                    {
                        Plugin.Log.Debug($"[LiveLog] Read {newLines.Count} new lines (pos={lastPosition})");
                        lastActivityTime = DateTime.UtcNow;
                        checkPending = false;

                        // Send lines to parser
                        await SendMessageAsync(new
                        {
                            message = "parse-lines",
                            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            lines = newLines,
                            scanning = false,
                            selectedRegion = regionStr,
                            raidsToUpload = Array.Empty<object>()
                        });

                        if (firstPass && uploadPreviousFights && newLines.Count > 1000)
                        {
                            await Task.Delay(Math.Max(500, newLines.Count / 100), cancellationToken);
                        }
                        else
                        {
                            await Task.Delay(100, cancellationToken);
                        }

                        // Scan for fight-end Director commands using centralized code set
                        if (!fightEndDetected)
                        {
                            foreach (var line in newLines)
                            {
                                if (line.StartsWith("33|") && FightEndDirectorCodes.Any(code => line.Contains($"|{code}|")))
                                {
                                    Plugin.Log.Information($"[LiveLog] Fight end detected (Director): {line.Substring(0, Math.Min(80, line.Length))}");
                                    fightEndDetected = true;
                                    fightEndDetectedTime = DateTime.UtcNow;
                                    break;
                                }
                            }
                        }
                    }

                    // Check for a newer log file periodically
                    // Reduced from 60s to 10s to minimize gap between old and new file
                    if ((DateTime.UtcNow - lastFileCheckTime).TotalSeconds > FileCheckIntervalSeconds)
                    {
                        lastFileCheckTime = DateTime.UtcNow;
                        var currentFiles = Directory.GetFiles(actualDirectory, "*.log");
                        if (currentFiles.Length > 0)
                        {
                            var newestFile = currentFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                            if (newestFile != logPath)
                            {
                                // Finish reading the remainder of the old file before switching
                                var (remainingLines, _) = await LogFileHelper.ReadNewLinesSharedAsync(logPath, lastPosition);
                                if (remainingLines.Count > 0)
                                {
                                    Plugin.Log.Information($"[LiveLog] Reading {remainingLines.Count} remaining lines from old file before switching");
                                    await SendMessageAsync(new
                                    {
                                        message = "parse-lines",
                                        id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                        lines = remainingLines,
                                        scanning = false,
                                        selectedRegion = regionStr,
                                        raidsToUpload = Array.Empty<object>()
                                    });
                                    await Task.Delay(100, cancellationToken);
                                }

                                Plugin.Log.Information($"[LiveLog] Switching to newer log file: {Path.GetFileName(newestFile)}");
                                logPath = newestFile;
                                lastPosition = 0;
                            }
                        }
                    }

                    // Check for fights when:
                    // 1. Fight end detected via Director command (after configurable delay for parser to finalize)
                    // 2. On first pass with uploadPreviousFights
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

                        lastFightCount = await CheckForFightsAsync(lastFightCount, onFightComplete, pushFightIfNeeded: true);
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
            if (parserProcess != null)
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

    private async Task<int> CheckForFightsAsync(int lastFightCount, Func<string, FightData, int, long, long, Task> onFightComplete, bool pushFightIfNeeded = false)
    {
        await SendMessageAsync(new
        {
            message = "collect-fights",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            pushFightIfNeeded = pushFightIfNeeded,
            scanningOnly = false
        });
        
        var fightsResult = await WaitForKeyAsync("fights", 5000);
        
        if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
        {
            var currentCount = fightsArray.GetArrayLength();
            
            if (currentCount > lastFightCount)
            {
                var newCount = currentCount - lastFightCount;
                Plugin.Log.Information($"[LiveLog] {newCount} NEW fight(s) detected!");
                
                await SendMessageAsync(new
                {
                    message = "collect-master-info",
                    id = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                
                var masterResult = await WaitForKeyAsync("actorsString", 5000);
                
                if (masterResult.HasValue)
                {
                    var fr = fightsResult.Value;
                    long globalStartTime = fr.TryGetProperty("startTime", out var gst) ? gst.GetInt64() : 0;
                    long globalEndTime = fr.TryGetProperty("endTime", out var get) ? get.GetInt64() : globalStartTime;
                    
                    var masterStr = BuildMasterTableString(fightsResult, masterResult.Value);
                    
                    int i = 0;
                    foreach (var fight in fightsArray.EnumerateArray())
                    {
                        if (i >= lastFightCount)
                        {
                            var eventsStr = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : "";
                            var fightData = new FightData
                            {
                                Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                                StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                                EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                                EventsString = eventsStr
                            };
                            
                            Plugin.Log.Information($"[LiveLog] Uploading: {fightData.Name} (segment {i + 1})");
                            await onFightComplete(masterStr, fightData, i + 1, globalStartTime, globalEndTime);
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
}
