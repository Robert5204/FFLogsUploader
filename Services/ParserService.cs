using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FFLogsPlugin.Services;

/// <summary>
/// Manages the FFLogs parser by calling Node.js subprocess directly.
/// </summary>
public class ParserService : IDisposable
{
    private const string FFLOGS_URL = "https://www.fflogs.com";
    
    private readonly Plugin plugin;
    private string? parserBundlePath;
    private Process? parserProcess;
    private readonly Queue<JsonElement> ipcQueue = new();
    private readonly object queueLock = new();

    // Use the authenticated HttpClient from FFLogsService
    private HttpClient HttpClient => plugin.FFLogsService.HttpClient;

    public ParserService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Dispose()
    {
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
    /// Ensures the parser bundle is downloaded and cached.
    /// </summary>
    private async Task EnsureParserAsync()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "FFLogsPlugin"
        );
        Directory.CreateDirectory(cacheDir);
        parserBundlePath = Path.Combine(cacheDir, "fflogs_parser_bundle.js");

        // If cached and fresh (< 24 hours), use it
        if (File.Exists(parserBundlePath) && File.GetLastWriteTimeUtc(parserBundlePath) > DateTime.UtcNow.AddHours(-24))
        {
            Plugin.Log.Information($"Using cached parser: {parserBundlePath}");
            return;
        }

        Plugin.Log.Information("Downloading FFLogs parser...");
        
        try
        {
            // Fetch the parser page
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var parserPageUrl = $"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&metersEnabled=false&liveFightDataEnabled=false";
            
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
            var glueMatch = Regex.Match(html, @"<script type=""text/javascript"">([\s\S]*?ipcCollectFights[\s\S]*?)</script>");
            var glueCode = glueMatch.Success ? glueMatch.Groups[1].Value : "";
            
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
            
            await File.WriteAllTextAsync(parserBundlePath, fullBundle);
            Plugin.Log.Information($"Parser downloaded and cached ({fullBundle.Length} bytes)");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to download parser");
            throw new Exception($"Failed to download parser: {ex.Message}");
        }
    }

    private async Task StartParserProcessAsync()
    {
        await EnsureParserAsync();

        // Kill any existing process to start fresh (prevents state accumulation)
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
        
        lock (queueLock) ipcQueue.Clear();

        parserProcess.OutputDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            if (args.Data.StartsWith("__IPC__:"))
            {
                try
                {
                    var json = args.Data.Substring(8);
                    var doc = JsonDocument.Parse(json);
                    lock (queueLock)
                    {
                        ipcQueue.Enqueue(doc.RootElement.Clone());
                    }
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
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < timeout)
        {
            lock (queueLock)
            {
                if (ipcQueue.Any(e => e.TryGetProperty("channel", out var c) && c.GetString() == "ready"))
                {
                    Plugin.Log.Information("Parser process ready");
                    ipcQueue.Clear();
                    return;
                }
            }
            await Task.Delay(100);
        }

        throw new Exception("Parser process did not start in time");
    }

    private async Task SendMessageAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        await parserProcess!.StandardInput.WriteLineAsync(json);
        await parserProcess.StandardInput.FlushAsync();
        Plugin.Log.Debug($"[Parser] Sent: {json.Substring(0, Math.Min(200, json.Length))}...");
    }

    private async Task<JsonElement?> WaitForChannelAsync(string channel, int timeoutMs = 10000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < timeout)
        {
            lock (queueLock)
            {
                for (int i = 0; i < ipcQueue.Count; i++)
                {
                    var item = ipcQueue.ElementAt(i);
                    if (item.TryGetProperty("channel", out var c) && c.GetString() == channel)
                    {
                        // Remove from queue
                        var temp = ipcQueue.ToList();
                        temp.RemoveAt(i);
                        ipcQueue.Clear();
                        foreach (var t in temp) ipcQueue.Enqueue(t);
                        return item;
                    }
                }
            }
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>
    /// Wait for a message that contains a specific key (like Python does)
    /// </summary>
    private async Task<JsonElement?> WaitForKeyAsync(string key, int timeoutMs = 10000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < timeout)
        {
            lock (queueLock)
            {
                for (int i = 0; i < ipcQueue.Count; i++)
                {
                    var item = ipcQueue.ElementAt(i);
                    // Check in data property (if wrapped by sendToHost)
                    if (item.TryGetProperty("data", out var dataObj) && dataObj.TryGetProperty(key, out _))
                    {
                        var temp = ipcQueue.ToList();
                        temp.RemoveAt(i);
                        ipcQueue.Clear();
                        foreach (var t in temp) ipcQueue.Enqueue(t);
                        return dataObj;
                    }
                    // Check directly (if from event.source.postMessage)
                    if (item.TryGetProperty(key, out _))
                    {
                        var temp = ipcQueue.ToList();
                        temp.RemoveAt(i);
                        ipcQueue.Clear();
                        foreach (var t in temp) ipcQueue.Enqueue(t);
                        return item;
                    }
                }
            }
            await Task.Delay(50);
        }
        return null;
    }

    /// <summary>
    /// Read all lines from a file while allowing other processes (like ACT) to continue writing.
    /// </summary>
    private async Task<string[]> ReadLinesWithSharingAsync(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            lines.Add(line);
        }
        
        return lines.ToArray();
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
        var lines = await ReadLinesWithSharingAsync(logPath);
        Plugin.Log.Debug($"[Parser] Read {lines.Length} lines");

        // Set report code
        await SendMessageAsync(new { message = "set-report-code", id = 0, reportCode });
        await Task.Delay(100);

        // Parse lines
        var regionMap = new Dictionary<int, string> { {1, "NA"}, {2, "EU"}, {3, "JP"}, {4, "CN"}, {5, "KR"} };
        var regionStr = regionMap.GetValueOrDefault(region, "NA");

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

        // Wait for fights data (look for 'fights' key like Python does)
        var fightsResult = await WaitForKeyAsync("fights", 15000);
        
        // Debug log what we got
        if (fightsResult.HasValue)
        {
            Plugin.Log.Debug($"[Parser] Got fights result with {(fightsResult.Value.TryGetProperty("fights", out var f) ? f.GetArrayLength() : 0)} fights");
        }
        else
        {
            Plugin.Log.Warning("[Parser] No fights result received!");
            // Debug: log what's in the queue
            lock (queueLock)
            {
                Plugin.Log.Debug($"[Parser] Queue has {ipcQueue.Count} messages");
                foreach (var msg in ipcQueue)
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

        // Wait for master data (look for 'actorsString' key like Python does)
        var masterResult = await WaitForKeyAsync("actorsString", 15000);

        // Debug log what we received
        if (fightsResult.HasValue)
        {
            var rawJson = fightsResult.Value.ToString();
            Plugin.Log.Debug($"[Parser] Fights result (first 500 chars): {rawJson.Substring(0, Math.Min(500, rawJson.Length))}");
        }
        else
        {
            Plugin.Log.Warning("[Parser] No fights result received!");
        }

        // Process results - try multiple formats
        // fights declared outside
        
        JsonElement? fightsArray = null;
        
        if (fightsResult.HasValue)
        {
            var fr = fightsResult.Value;
            
            // Try data.fights (wrapped format)
            if (fr.TryGetProperty("data", out var dataObj) && dataObj.TryGetProperty("fights", out var f1))
            {
                fightsArray = f1;
                Plugin.Log.Debug("[Parser] Found fights in data.fights");
            }
            // Try direct fights property
            else if (fr.TryGetProperty("fights", out var f2))
            {
                fightsArray = f2;
                Plugin.Log.Debug("[Parser] Found fights directly");
            }
        }
        
        if (fightsArray.HasValue)
        {
            foreach (var fight in fightsArray.Value.EnumerateArray())
            {
                fights.Add(new FightData
                {
                    Name = fight.TryGetProperty("name", out var n) ? n.GetString() ?? "Unknown" : "Unknown",
                    StartTime = fight.TryGetProperty("startTime", out var s) ? s.GetInt64() : 0,
                    EndTime = fight.TryGetProperty("endTime", out var e) ? e.GetInt64() : 0,
                    EventsString = fight.TryGetProperty("eventsString", out var ev) ? ev.GetString() ?? "" : ""
                });
            }
        }

        Plugin.Log.Information($"[Parser] Extracted {fights.Count} fights");

        // Get global timestamps from fights result
        // Get global timestamps from fights result
        // vars startTime, endTime declared outside
        if (fightsResult.HasValue)
        {
            var fr = fightsResult.Value;
            startTime = fr.TryGetProperty("startTime", out var st) ? st.GetInt64() : 0;
            endTime = fr.TryGetProperty("endTime", out var et) ? et.GetInt64() : 0;
        }

        // Build master table string - header comes from fights, sections from master
        // Build master table string - header comes from fights, sections from master
        // var masterStr declared outside
        if (masterResult.HasValue)
        {
            // Get header info from fights result (logVersion, gameVersion, logFileDetails)
            JsonElement? fightsHeader = fightsResult;
            masterStr = BuildMasterTableString(fightsHeader, masterResult.Value);
        }

        }
        finally
        {
            StopParser(); // Auto-close Node process after manual upload
        }

        return (masterStr, fights, startTime, endTime);
    }

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

        // Sections in order: actorsString, abilitiesString, tuplesString, petsString (matching Python)
        string[] sectionKeys = { "actorsString", "abilitiesString", "tuplesString", "petsString" };
        
        foreach (var key in sectionKeys)
        {
            var value = "";
            if (masterData.TryGetProperty(key, out var prop))
            {
                value = prop.GetString() ?? "";
            }
            
            // Get lines - don't trim, just filter empty lines
            var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Where(l => !string.IsNullOrWhiteSpace(l))
                             .ToList();
            
            // Add count
            parts.Add(lines.Count.ToString());
            
            // Add lines
            parts.AddRange(lines);
        }

        var result = string.Join("\n", parts);
        Plugin.Log.Debug($"[Parser] Master table: {result.Length} chars, header: {parts[0]}");
        return result;
    }

    /// <summary>
    /// Start live logging - monitors the latest log file and uploads fights as they complete.
    /// </summary>
    public async Task StartLiveLogAsync(
        string logDirectory,
        int region,
        bool uploadPreviousFights,
        Func<string, FightData, int, long, long, Task> onFightComplete,  // Added startTime, endTime
        CancellationToken cancellationToken = default)
    {
        await StartParserProcessAsync();
        try
        {
        
        // Note: Report code is no longer needed here - it's set when uploading

        // Handle case where logDirectory might actually be a file path
        var actualDirectory = logDirectory;
        if (File.Exists(logDirectory))
        {
            actualDirectory = Path.GetDirectoryName(logDirectory) ?? logDirectory;
        }
        else if (!Directory.Exists(logDirectory))
        {
            // Maybe it's a path to a deleted file - extract directory
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

        var regionStr = region switch { 1 => "NA", 2 => "EU", 3 => "JP", 4 => "OC", _ => "NA" };
        
        int lastFightCount = 0;
        DateTime lastActivityTime = DateTime.UtcNow;
        DateTime lastFileCheckTime = DateTime.UtcNow;
        bool checkPending = false;
        long lastPosition = 0;
        bool firstPass = true;

        // If not uploading previous fights, skip to end of file
        if (!uploadPreviousFights)
        {
            var fileInfo = new FileInfo(logPath);
            lastPosition = fileInfo.Length;
            Plugin.Log.Information($"[LiveLog] Skipping to end of file (position {lastPosition})");
        }

        // Main loop
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Read new lines from log file
                var (newLines, newPosition) = await ReadNewLinesAsync(logPath, lastPosition);
                lastPosition = newPosition;
                
                if (newLines.Count > 0)
                {
                    Plugin.Log.Debug($"[LiveLog] Read {newLines.Count} new lines");
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
                    
                    // Give parser time to process
                    // If this is the initial large read, wait longer
                    if (firstPass && uploadPreviousFights && newLines.Count > 1000)
                    {
                         await Task.Delay(Math.Max(500, newLines.Count / 100), cancellationToken);
                    }
                    else
                    {
                         await Task.Delay(100, cancellationToken);
                    }
                    
                    // Note: Don't clear IPC queue here - we need the parser responses later!
                }

                // Check for a newer log file every 60 seconds (for multi-day sessions)
                if ((DateTime.UtcNow - lastFileCheckTime).TotalSeconds > 60)
                {
                    lastFileCheckTime = DateTime.UtcNow;
                    var currentFiles = Directory.GetFiles(logDirectory, "*.log");
                    if (currentFiles.Length > 0)
                    {
                        var newestFile = currentFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                        if (newestFile != logPath)
                        {
                            Plugin.Log.Information($"[LiveLog] Detected newer log file: {Path.GetFileName(newestFile)}");
                            logPath = newestFile;
                            lastPosition = 0; // Start from beginning of new file
                        }
                    }
                }
                
                // Check if idle for 5+ seconds
                // Check if idle for 5+ seconds OR if we should force a check (initial read)
                var idleTime = (DateTime.UtcNow - lastActivityTime).TotalSeconds;
                bool forceCheck = firstPass && uploadPreviousFights;

                if ((idleTime > 5.0 || forceCheck) && !checkPending)
                {
                    if (forceCheck) firstPass = false;
                    checkPending = true;
                    if (forceCheck)
                        Plugin.Log.Debug($"[LiveLog] Initial read complete - checking for fights immediately");
                    else
                        Plugin.Log.Debug($"[LiveLog] Idle for {idleTime:F1}s - checking for new fights");
                    
                    // Collect fights
                    lastFightCount = await CheckForFightsAsync(lastFightCount, onFightComplete);
                }
                
                // Small delay between iterations
                await Task.Delay(500, cancellationToken);
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
                // Force a check with a short timeout AND push any pending fight
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
             StopParser(); // Auto-close Node process after live log session ends
        }
    }

    private async Task<int> CheckForFightsAsync(int lastFightCount, Func<string, FightData, int, long, long, Task> onFightComplete, bool pushFightIfNeeded = false)
    {
        // Collect fights
        await SendMessageAsync(new
        {
            message = "collect-fights",
            id = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            pushFightIfNeeded = pushFightIfNeeded,
            scanningOnly = false
        });
        
        // Use a shorter timeout since we might be closing
        var fightsResult = await WaitForKeyAsync("fights", 5000);
        
        if (fightsResult.HasValue && fightsResult.Value.TryGetProperty("fights", out var fightsArray))
        {
            var currentCount = fightsArray.GetArrayLength();
            
            if (currentCount > lastFightCount)
            {
                var newCount = currentCount - lastFightCount;
                Plugin.Log.Information($"[LiveLog] {newCount} NEW fight(s) detected!");
                
                // Get master data
                await SendMessageAsync(new
                {
                    message = "collect-master-info",
                    id = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                
                var masterResult = await WaitForKeyAsync("actorsString", 5000);
                
                if (masterResult.HasValue)
                {
                    // Get global timestamps from fights result
                    var fr = fightsResult.Value;
                    long globalStartTime = fr.TryGetProperty("startTime", out var gst) ? gst.GetInt64() : 0;
                    long globalEndTime = fr.TryGetProperty("endTime", out var get) ? get.GetInt64() : globalStartTime;
                    
                    // Build master string
                    var masterStr = BuildMasterTableString(fightsResult, masterResult.Value);
                    
                    // Upload only NEW fights
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

    /// <summary>
    /// Read new lines from a log file starting from a position.
    /// </summary>
    private async Task<(List<string> lines, long newPosition)> ReadNewLinesAsync(string logPath, long position)
    {
        var lines = new List<string>();
        var newPosition = position;
        
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            // If file was truncated (new log), reset position
            if (stream.Length < position)
            {
                newPosition = 0;
                Plugin.Log.Debug("[LiveLog] File truncated, resetting position");
            }
            
            stream.Seek(newPosition, SeekOrigin.Begin);
            
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var cleanLine = line.Replace("\0", "").Trim();
                if (!string.IsNullOrEmpty(cleanLine))
                    lines.Add(cleanLine);
            }
            
            newPosition = stream.Position;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[LiveLog] Read error: {ex.Message}");
        }
        
        return (lines, newPosition);
    }
}
