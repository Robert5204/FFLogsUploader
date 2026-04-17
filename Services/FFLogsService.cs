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
using FFLogsPlugin.Handlers;
using FFLogsPlugin.Models;

namespace FFLogsPlugin.Services;

/// <summary>
/// Handles all FFLogs API communication: login, report creation, uploads.
/// </summary>
public class FFLogsService
{
    private const string FFLOGS_URL = "https://www.fflogs.com";
    private const string CLIENT_VERSION = "9.0.33";
    private const int PARSER_VERSION = 1072;
    private const int MaxRetries = 3;
    
    private readonly Plugin plugin;
    private readonly CookieContainer cookies;

    // Expose HttpClient for other services that need authenticated access
    public HttpClient HttpClient { get; }
    
    public bool IsLoggedIn { get; private set; }
    public string? CurrentReportCode { get; private set; }
    public string? Username { get; private set; }
    public List<GuildInfo> Guilds { get; private set; } = new();

    // Recently uploaded report codes (for Parse Viewer)
    public List<string> RecentReportCodes { get; } = new();
    private const int MaxRecentReports = 10;

    // Live logging state
    public bool IsLiveLogging { get; private set; }
    public int LiveFightCount { get; private set; }
    private CancellationTokenSource? liveLogCts;
    private Task? liveLogTask;

    public FFLogsService(Plugin plugin)
    {
        this.plugin = plugin;
        
        cookies = new CookieContainer();
        var innerHandler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        
        // Wrap with XSRF handler so every request automatically gets the token
        var xsrfHandler = new XsrfDelegatingHandler(cookies, new Uri(FFLOGS_URL), innerHandler);
        
        HttpClient = new HttpClient(xsrfHandler)
        {
            BaseAddress = new Uri(FFLOGS_URL)
        };
        
        // Match the official FFLogs client headers exactly
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) FFLogsUploader/{CLIENT_VERSION} Chrome/138.0.7204.251 Electron/37.7.0 Safari/537.36");
        HttpClient.DefaultRequestHeaders.Add("Accept", "*/*");
        HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\"");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        HttpClient.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Storage-Access", "active");
    }

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            Plugin.Log.Information("[Login] Starting login via desktop-client API...");
            
            var payload = new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
                ["version"] = CLIENT_VERSION
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

            // XSRF header is now handled automatically by XsrfDelegatingHandler

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
        // No session fields to clear — XSRF is managed by the handler
    }

    public async Task<string> CreateReportAsync(string filename, string description, int visibility, int region, string? guildId = null)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
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

        // 3. Upload each fight segment (with master table before each).
        // endTime is per-fight (global start + last event relative time) so each
        // segment has distinct boundaries on the server — matches official client.
        for (int i = 0; i < fights.Count; i++)
        {
            int segmentId = i + 1;
            long fightEndTime = ComputeFightEndTime(startTime, fights[i].EventsString, endTime);
            await WithRetryAsync(() => UploadMasterTableAsync(reportCode, masterData, segmentId));
            await WithRetryAsync(() => UploadSegmentAsync(reportCode, fights[i], segmentId, startTime, fightEndTime));
        }

        // 4. Terminate the report
        await TerminateReportAsync(reportCode);

        TrackReportCode(reportCode);
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

        liveLogCts = new CancellationTokenSource();
        IsLiveLogging = true;
        LiveFightCount = 0;
        CurrentReportCode = null;

        liveLogTask = Task.Run(async () =>
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

                        // Inform the parser so subsequent collect-master-info calls
                        // can be validated against the expected report code.
                        plugin.ParserService.SetReportCode(reportCode);
                    }

                    await WithRetryAsync(() => UploadMasterTableAsync(reportCode, masterData, segmentId));
                    await WithRetryAsync(() => UploadSegmentAsync(reportCode, fight, segmentId, startTime, endTime, isLive: true));
                    LiveFightCount++;
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
                if (reportCode != null)
                {
                    await TerminateReportAsync(reportCode);
                    TrackReportCode(reportCode);
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

    /// <summary>
    /// Waits for any active live log task to finish. Called during plugin disposal
    /// to ensure the V8 engine isn't disposed while still in use.
    /// </summary>
    public async Task WaitForLiveLogAsync(int timeoutMs = 5000)
    {
        if (liveLogTask == null) return;

        // Signal cancellation first
        liveLogCts?.Cancel();

        // Wait for the task to complete (with timeout to avoid hanging disposal)
        try
        {
            await liveLogTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            Plugin.Log.Warning("[LiveLog] Live log task did not finish within timeout during disposal");
        }
        catch (Exception)
        {
            // Task already completed with error — that's fine
        }
    }

    private void TrackReportCode(string code)
    {
        RecentReportCodes.Remove(code);
        RecentReportCodes.Insert(0, code);
        while (RecentReportCodes.Count > MaxRecentReports)
            RecentReportCodes.RemoveAt(RecentReportCodes.Count - 1);
    }

    public void StopLiveLog()
    {
        if (!IsLiveLogging)
            return;
            
        Plugin.Log.Information("[LiveLog] Stopping live logging...");
        liveLogCts?.Cancel();
    }

    /// <summary>
    /// Executes an async action with exponential backoff retry.
    /// </summary>
    private async Task WithRetryAsync(Func<Task> action, int maxRetries = MaxRetries)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                Plugin.Log.Warning($"[Retry] Attempt {attempt + 1}/{maxRetries} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay);
            }
        }
    }

    private static string GenerateWebKitBoundary()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        var suffix = new char[16];
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = chars[random.Next(chars.Length)];
        return $"----WebKitFormBoundary{new string(suffix)}";
    }

    /// <summary>
    /// Computes a per-fight endTime as globalStart + last-event relative time, parsed
    /// from the fight's eventsString buffer. Falls back to <paramref name="fallback"/>
    /// if the buffer contains no parseable event rows.
    /// </summary>
    private static long ComputeFightEndTime(long globalStart, string eventsString, long fallback)
    {
        if (string.IsNullOrEmpty(eventsString)) return fallback;

        int end = eventsString.Length;
        while (end > 0 && (eventsString[end - 1] == '\n' || eventsString[end - 1] == '\r'))
            end--;
        if (end == 0) return fallback;

        int firstNewline = eventsString.IndexOf('\n');
        int secondNewline = firstNewline >= 0 ? eventsString.IndexOf('\n', firstNewline + 1) : -1;
        if (secondNewline < 0) return fallback;

        int lineStart = eventsString.LastIndexOf('\n', end - 1) + 1;
        if (lineStart <= secondNewline) return fallback;

        int pipe = eventsString.IndexOf('|', lineStart, end - lineStart);
        if (pipe < 0) return fallback;

        return long.TryParse(eventsString.AsSpan(lineStart, pipe - lineStart), out var rel)
            ? globalStart + rel
            : fallback;
    }

    private static HttpContent CreateStringPart(string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\""
        };
        return content;
    }

    private static HttpContent CreateFilePart(string name, string filename, byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.Clear();
        // Add headers in exact order to match the official client
        content.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{name}\"; filename=\"{filename}\"");
        content.Headers.TryAddWithoutValidation("Content-Type", "application/zip");
        return content;
    }

    private async Task UploadMasterTableAsync(string reportCode, string masterTableContent, int segmentId = 1, bool isRealTime = false)
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
        var boundary = GenerateWebKitBoundary();
        using var content = new MultipartFormDataContent(boundary);
        // .NET quotes the boundary by default; the FFLogs server expects an
        // unquoted boundary (matching the official Electron uploader).
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        content.Add(CreateStringPart("segmentId", segmentId.ToString()));
        content.Add(CreateStringPart("isRealTime", isRealTime.ToString().ToLower()));
        content.Add(CreateFilePart("logfile", "blob", zipBytes));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/set-report-master-table/{reportCode}")
        {
            Content = content
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Master table uploaded for {reportCode}");
    }

    private async Task UploadSegmentAsync(string reportCode, FightData fight, int segmentId, long startTime, long endTime, bool isLive = false)
    {
        // Format events: header + count + events
        var lines = fight.EventsString.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var eventsContent = $"{fight.LogVersion}|{fight.GameVersion}\n{lines.Length}\n{fight.EventsString}";
        
        using var memoryStream = new MemoryStream();
        using (var zipArchive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zipArchive.CreateEntry("log.txt");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(eventsContent);
        }

        var zipBytes = memoryStream.ToArray();
        
        // Official client sends logfile first, then parameters as a JSON field
        var parameters = JsonSerializer.Serialize(new
        {
            startTime,
            endTime,
            mythic = 0,
            isLiveLog = isLive,
            isRealTime = false,
            inProgressEventCount = 0,
            segmentId
        });

        var boundary = GenerateWebKitBoundary();
        using var formContent = new MultipartFormDataContent(boundary);
        // .NET quotes the boundary by default; the FFLogs server expects an
        // unquoted boundary (matching the official Electron uploader).
        formContent.Headers.Remove("Content-Type");
        formContent.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        formContent.Add(CreateFilePart("logfile", "blob", zipBytes));
        formContent.Add(CreateStringPart("parameters", parameters));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/add-report-segment/{reportCode}")
        {
            Content = formContent
        };
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Segment {segmentId} uploaded for {reportCode} (isLive={isLive})");
    }

    private async Task TerminateReportAsync(string reportCode)
    {
        try
        {
            await WithRetryAsync(async () =>
            {
                var response = await HttpClient.PostAsync($"/desktop-client/terminate-report/{reportCode}", null);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Terminate failed with status {response.StatusCode}");
                }
                Plugin.Log.Debug($"Report terminated: {reportCode}");
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[TerminateReport] Failed to terminate report {reportCode} after {MaxRetries} retries: {ex.Message}. " +
                               "The report may be left in an incomplete state on FFLogs — check the website.");
        }
    }
}

public class FightData
{
    public string Name { get; set; } = string.Empty;
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public string EventsString { get; set; } = string.Empty;
    public int LogVersion { get; set; } = 72;
    public int GameVersion { get; set; } = 1;
}

public class GuildInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
