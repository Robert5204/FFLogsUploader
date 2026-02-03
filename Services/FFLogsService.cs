using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace FFLogsPlugin.Services;

/// <summary>
/// Handles all FFLogs API communication: login, report creation, uploads.
/// Ported from the Python fflogs_client_emulator.py
/// </summary>
public class FFLogsService
{
    private const string FFLOGS_URL = "https://www.fflogs.com";
    private const string CLIENT_VERSION = "8.19.39";
    private const int PARSER_VERSION = 1072;
    
    private readonly Plugin plugin;
    private readonly CookieContainer cookies;

    // Expose HttpClient for other services that need authenticated access
    public HttpClient HttpClient { get; }
    
    public bool IsLoggedIn { get; private set; }
    public string? CurrentReportCode { get; private set; }
    public string? Username { get; private set; }
    public List<GuildInfo> Guilds { get; private set; } = new();

    // Live logging state
    public bool IsLiveLogging { get; private set; }
    public int LiveFightCount { get; private set; }
    private CancellationTokenSource? liveLogCts;

    private void UpdateCsrfHeader()
    {
        var xsrfCookie = cookies.GetCookies(new Uri(FFLOGS_URL))["XSRF-TOKEN"]?.Value;
        if (!string.IsNullOrEmpty(xsrfCookie))
        {
            var decodedXsrf = HttpUtility.UrlDecode(xsrfCookie);
            HttpClient.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
            HttpClient.DefaultRequestHeaders.Add("X-XSRF-TOKEN", decodedXsrf);
        }
    }

    public FFLogsService(Plugin plugin)
    {
        this.plugin = plugin;
        
        cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        
        HttpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(FFLOGS_URL)
        };
        
        // Match the official FFLogs client headers exactly
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) FFLogsUploader/8.17.115 Chrome/138.0.7204.251 Electron/37.7.0 Safari/537.36");
        HttpClient.DefaultRequestHeaders.Add("Accept", "*/*");
        HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\"");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
    }

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            Plugin.Log.Information("[Login] Starting login via desktop-client API...");
            
            // Use the official desktop client API endpoint (avoids CSRF/Cloudflare issues)
            var payload = new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
                ["version"] = "8.17.115"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            Plugin.Log.Debug("[Login] Posting to /desktop-client/log-in...");
            var response = await HttpClient.PostAsync("/desktop-client/log-in", content);
            
            Plugin.Log.Debug($"[Login] Response status: {response.StatusCode}");
            var responseBody = await response.Content.ReadAsStringAsync();
            Plugin.Log.Debug($"[Login] Response: {responseBody.Substring(0, Math.Min(500, responseBody.Length))}");

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Plugin.Log.Error($"[Login] Login failed with status {response.StatusCode}");
                return false;
            }

            // Parse user data from response
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var user = doc.RootElement.GetProperty("user");
                Username = user.GetProperty("userName").GetString();
                Plugin.Log.Information($"[Login] Logged in as: {Username}");
                
                // Parse guilds
                Guilds.Clear();
                if (user.TryGetProperty("guilds", out var guildsArray))
                {
                    foreach (var guild in guildsArray.EnumerateArray())
                    {
                        var guildInfo = new GuildInfo
                        {
                            Id = guild.GetProperty("id").GetInt32().ToString(),
                            Name = guild.GetProperty("name").GetString() ?? "Unknown"
                        };
                        Guilds.Add(guildInfo);
                        Plugin.Log.Debug($"[Login] Found guild: {guildInfo.Name}");
                    }
                }
                Plugin.Log.Information($"[Login] Found {Guilds.Count} guilds");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Login] Could not parse user data: {ex.Message}");
            }

            // Get cookies from response
            var responseCookies = cookies.GetCookies(new Uri(FFLOGS_URL));
            Plugin.Log.Debug($"[Login] Cookies received: {responseCookies.Count}");
            foreach (Cookie c in responseCookies)
            {
                Plugin.Log.Debug($"[Login] Cookie: {c.Name}");
            }

            // Refresh the session token (required after login)
            Plugin.Log.Debug("[Login] Refreshing session token...");
            var tokenResponse = await HttpClient.PostAsync("/desktop-client/token", null);
            Plugin.Log.Debug($"[Login] Token refresh status: {tokenResponse.StatusCode}");
            
            if (tokenResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Plugin.Log.Warning("[Login] Token refresh failed, but login may still work");
            }

            // Update XSRF header from cookies
            var xsrfCookie = cookies.GetCookies(new Uri(FFLOGS_URL))["XSRF-TOKEN"]?.Value;
            if (!string.IsNullOrEmpty(xsrfCookie))
            {
                var decodedXsrf = HttpUtility.UrlDecode(xsrfCookie);
                HttpClient.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
                HttpClient.DefaultRequestHeaders.Add("X-XSRF-TOKEN", decodedXsrf);
                Plugin.Log.Debug("[Login] XSRF header set");
            }

            IsLoggedIn = true;
            Plugin.Log.Information("[Login] Login successful!");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Login] Login failed with exception");
            return false;
        }
    }

    public void Logout()
    {
        IsLoggedIn = false;
        plugin.Configuration.SessionCookie = null;
        plugin.Configuration.XsrfToken = null;
        plugin.Configuration.Save();
    }

    public async Task<string> CreateReportAsync(string filename, string description, int visibility, int region, string? guildId = null)
    {
        // Update CSRF header before request
        UpdateCsrfHeader();
        
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Use desktop-client endpoint and payload format matching Python emulator
        var payload = new Dictionary<string, object?>
        {
            ["clientVersion"] = CLIENT_VERSION,
            ["parserVersion"] = PARSER_VERSION,
            ["startTime"] = ts,
            ["endTime"] = ts,
            ["guildId"] = string.IsNullOrEmpty(guildId) ? null : int.Parse(guildId),
            ["fileName"] = Path.GetFileName(filename),
            ["serverOrRegion"] = region,
            ["visibility"] = visibility,
            ["reportTagId"] = null,
            ["description"] = description
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Plugin.Log.Debug($"[CreateReport] POST /desktop-client/create-report: {json}");
        var response = await HttpClient.PostAsync("/desktop-client/create-report", content);
        
        var responseBody = await response.Content.ReadAsStringAsync();
        Plugin.Log.Debug($"[CreateReport] Response: {response.StatusCode} - {responseBody}");
        
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new Exception($"Create report failed: {response.StatusCode} - {responseBody}");
        }

        // Response may be just the code as a string or JSON with "code" field
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            CurrentReportCode = doc.RootElement.GetProperty("code").GetString();
        }
        catch
        {
            CurrentReportCode = responseBody.Trim().Trim('"');
        }
        
        Plugin.Log.Information($"[CreateReport] Created report: {CurrentReportCode}");
        return CurrentReportCode ?? throw new Exception("No report code in response");
    }

    public async Task<string> UploadLogAsync(string logPath, int region, int visibility, string? guildId, string description)
    {
        // 1. Create report
        var reportCode = await CreateReportAsync(logPath, description, visibility, region, guildId);

        // 2. Process log with parser
        var (masterData, fights, startTime, endTime) = await plugin.ParserService.ProcessLogAsync(logPath, reportCode, region);

        // 3. Upload each fight segment (with master table before each, like Python does)
        for (int i = 0; i < fights.Count; i++)
        {
            // Upload master table before each segment (interleaved)
            await UploadMasterTableAsync(reportCode, masterData);
            
            // Upload segment with global timestamps (not per-fight)
            await UploadSegmentAsync(reportCode, fights[i], i + 1, startTime, endTime);
        }

        // 4. Terminate the report
        await TerminateReportAsync(reportCode);

        Plugin.Log.Information($"Upload complete: {reportCode}");
        return reportCode;
    }

    public async Task StartLiveLogAsync(string logDirectory, int region, int visibility, string? guildId, string description, bool uploadPreviousFights = true)
    {
        if (IsLiveLogging)
        {
            Plugin.Log.Warning("Live logging already in progress");
            return;
        }

        // Setup cancellation (don't create report yet - wait for first fight)
        liveLogCts = new CancellationTokenSource();
        IsLiveLogging = true;
        LiveFightCount = 0;
        CurrentReportCode = null; // Reset

        // Start live monitoring (runs in background)
        _ = Task.Run(async () =>
        {
            string? reportCode = null;
            
            try
            {
                await plugin.ParserService.StartLiveLogAsync(logDirectory, region, uploadPreviousFights, async (masterData, fight, segmentId, startTime, endTime) =>
                {
                    // Create report lazily on first fight
                    if (reportCode == null)
                    {
                        Plugin.Log.Information("[LiveLog] First fight detected - creating report...");
                        reportCode = await CreateReportAsync("live_log", description, visibility, region, guildId);
                        CurrentReportCode = reportCode;
                    }
                    
                    await UploadMasterTableAsync(reportCode, masterData);
                    await UploadSegmentAsync(reportCode, fight, segmentId, startTime, endTime, isLive: true);
                    LiveFightCount = segmentId;
                }, liveLogCts.Token);
            }
            catch (OperationCanceledException)
            {
                Plugin.Log.Information("[LiveLog] Cancelled");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[LiveLog] Error");
            }
            finally
            {
                // Terminate the report only if one was created
                if (reportCode != null)
                {
                    try
                    {
                        await TerminateReportAsync(reportCode);
                    }
                    catch { }
                    Plugin.Log.Information($"[LiveLog] Live logging ended. Report: {reportCode}");
                }
                else
                {
                    Plugin.Log.Information("[LiveLog] Live logging ended. No fights were uploaded.");
                }
                
                IsLiveLogging = false;
            }
        });
    }

    public void StopLiveLog()
    {
        if (!IsLiveLogging)
            return;
            
        Plugin.Log.Information("[LiveLog] Stopping live logging...");
        liveLogCts?.Cancel();
    }

    private async Task UploadMasterTableAsync(string reportCode, string masterTableContent)
    {
        using var memoryStream = new MemoryStream();
        using (var zipArchive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zipArchive.CreateEntry("log.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(masterTableContent);
        }

        var zipBytes = memoryStream.ToArray();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(zipBytes), "logfile", "blob");

        var response = await HttpClient.PostAsync($"/desktop-client/set-report-master-table/{reportCode}", content);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Master table uploaded for {reportCode}");
    }

    private async Task UploadSegmentAsync(string reportCode, FightData fight, int segmentId, long startTime, long endTime, bool isLive = false)
    {
        // Format events like Python: header + count + events
        var lines = fight.EventsString.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var content = $"72|1\n{lines.Length}\n{fight.EventsString}";
        
        using var memoryStream = new MemoryStream();
        using (var zipArchive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zipArchive.CreateEntry("log.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(content);
        }

        var zipBytes = memoryStream.ToArray();
        
        // Use global timestamps from fights data
        var parameters = new Dictionary<string, object>
        {
            ["startTime"] = startTime,
            ["endTime"] = endTime,
            ["mythic"] = 0,
            ["isLiveLog"] = isLive,      // True for live logging
            ["isRealTime"] = isLive,     // True for live logging
            ["inProgressEventCount"] = 0,
            ["segmentId"] = segmentId
        };

        using var formContent = new MultipartFormDataContent();
        formContent.Add(new ByteArrayContent(zipBytes), "logfile", "blob");
        formContent.Add(new StringContent(JsonSerializer.Serialize(parameters)), "parameters");

        var response = await HttpClient.PostAsync($"/desktop-client/add-report-segment/{reportCode}", formContent);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Segment {segmentId} uploaded for {reportCode} (isLive={isLive})");
    }

    private async Task TerminateReportAsync(string reportCode)
    {
        var response = await HttpClient.PostAsync($"/desktop-client/terminate-report/{reportCode}", null);
        // Don't throw on failure - terminate is optional
        Plugin.Log.Debug($"Report terminated: {reportCode} (status: {response.StatusCode})");
    }
}

public class FightData
{
    public string Name { get; set; } = string.Empty;
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public string EventsString { get; set; } = string.Empty;
}

public class GuildInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
