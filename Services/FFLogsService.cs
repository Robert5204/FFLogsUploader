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
    // Must match a currently-supported Archon App Lite version. FFLogs deprecated the
    // standalone "FFLogsUploader" client and now gates the desktop-client endpoints on
    // the Archon app's version/User-Agent — an out-of-date value is rejected server-side.
    private const string CLIENT_VERSION = "9.3.85";
    private const int MaxRetries = 3;
    
    private readonly Plugin plugin;
    private readonly CookieContainer cookies;

    // Expose HttpClient for other services that need authenticated access
    public HttpClient HttpClient { get; }
    
    public bool IsLoggedIn { get; private set; }
    public string? CurrentReportCode { get; private set; }
    public string? Username { get; private set; }
    public List<GuildInfo> Guilds { get; private set; } = new();

    // From the login response's enabledFeatures. When true the account may upload fights
    // while they're still in progress (real-time live logging); otherwise in-progress
    // fights are only tracked for status and uploaded once they commit — matching Archon.
    public bool RealTimeLiveLoggingEnabled { get; private set; }

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
            // Archon advertises "gzip, deflate, br, zstd". We advertise everything the
            // Electron client does that .NET can also transparently decode — gzip,
            // deflate, and brotli. zstd is intentionally omitted: HttpClient cannot
            // decompress it, and advertising it would risk the server returning an
            // undecodable body. In practice FFLogs negotiates down to brotli anyway.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        // The Archon uploader authenticates these desktop-client routes purely with the
        // session cookies (XSRF-TOKEN, wcl_session, remember_web) — the CookieContainer
        // sends those automatically. It never adds an X-XSRF-TOKEN *header*, so we don't
        // either; the routes are CSRF-exempt server-side. (The former XsrfDelegatingHandler
        // is left in the project unused in case a future endpoint needs it.)
        HttpClient = new HttpClient(innerHandler)
        {
            BaseAddress = new Uri(FFLOGS_URL)
        };

        // Only the headers Archon sends on *every* request live here. The Accept,
        // Sec-Fetch-Mode/Dest, sec-ch-ua*, Content-Type and Upgrade-Insecure-Requests
        // headers vary by request type (JSON API vs multipart upload vs iframe
        // navigation vs no-cors script) and are applied per request in
        // ApplyArchonHeaders so each call matches the real client byte-for-byte.
        HttpClient.DefaultRequestHeaders.Add("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ArchonAppLite/{CLIENT_VERSION} Chrome/138.0.7204.251 Electron/37.9.0 Safari/537.36");
        HttpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        HttpClient.DefaultRequestHeaders.Add("Sec-Fetch-Storage-Access", "active");
    }

    /// <summary>
    /// The kinds of request the Archon uploader makes, each with its own header
    /// fingerprint (Accept, Sec-Fetch-Mode/Dest, and whether sec-ch-ua* client hints
    /// are present). Derived from Fiddler captures of Archon App Lite 9.3.85.
    /// </summary>
    public enum RequestKind
    {
        /// <summary>POST with a JSON body (log-in, create-report).</summary>
        JsonApi,
        /// <summary>POST with an empty body (token/v2, terminate-report).</summary>
        EmptyPost,
        /// <summary>POST multipart/form-data upload (set-report-master-table, add-report-segment).</summary>
        Multipart,
        /// <summary>GET of the parser HTML page — loaded as an iframe navigation.</summary>
        ParserPage,
        /// <summary>GET of the parser JS from assets.rpglogs.com — a no-cors script fetch.</summary>
        Script,
    }

    /// <summary>
    /// Applies the per-request headers Archon sends for the given <paramref name="kind"/>,
    /// so plugin requests match the official uploader's fingerprint. The universal
    /// headers (User-Agent, Accept-Language, Sec-Fetch-Site, Sec-Fetch-Storage-Access)
    /// come from DefaultRequestHeaders; Content-Type is set on the request content.
    /// </summary>
    public static void ApplyArchonHeaders(HttpRequestMessage request, RequestKind kind)
    {
        // Chromium sends sec-ch-ua* client hints on every fetch/XHR/script request,
        // but omits them on top-level/iframe navigations — which is how Archon loads
        // the parser page.
        if (kind != RequestKind.ParserPage)
        {
            request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\"");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        }

        switch (kind)
        {
            case RequestKind.JsonApi:
            case RequestKind.EmptyPost:
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                break;
            case RequestKind.Multipart:
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                break;
            case RequestKind.ParserPage:
                request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
                break;
            case RequestKind.Script:
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
                break;
        }
    }

    /// <summary>
    /// Builds a JSON request body with a bare <c>application/json</c> Content-Type
    /// (no <c>; charset=utf-8</c> suffix) to match the official client exactly, while
    /// still encoding the payload as UTF-8.
    /// </summary>
    private static StringContent JsonBody(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>An empty POST body: Content-Length 0, no Content-Type (matches Archon).</summary>
    private static ByteArrayContent EmptyBody() => new(Array.Empty<byte>());

    public async Task<bool> LoginAsync(string email, string password)
    {
        try
        {
            Plugin.Log.Information("[Login] Starting login via desktop-client API...");
            
            var payload = new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
                ["version"] = CLIENT_VERSION,
                ["clientTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            var json = JsonSerializer.Serialize(payload);

            Plugin.Log.Debug("[Login] Posting to /desktop-client/log-in...");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/log-in") { Content = JsonBody(json) };
            ApplyArchonHeaders(request, RequestKind.JsonApi);
            var response = await HttpClient.SendAsync(request);
            
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

                // Account feature flags (sibling of "user" in the response).
                RealTimeLiveLoggingEnabled = false;
                if (doc.RootElement.TryGetProperty("enabledFeatures", out var features) &&
                    features.TryGetProperty("realTimeLiveLogging", out var rtll))
                {
                    RealTimeLiveLoggingEnabled = rtll.ValueKind == JsonValueKind.True;
                }
                Plugin.Log.Information($"[Login] realTimeLiveLogging={RealTimeLiveLoggingEnabled}");

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

            // Refresh the session token (required after login). The Archon app moved
            // this from /desktop-client/token to /desktop-client/token/v2; the old path
            // is disabled. The v2 response body is a JWT used for websocket features we
            // don't need — we call it only for the XSRF/session cookie refresh side effect.
            Plugin.Log.Debug("[Login] Refreshing session token...");
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/token/v2") { Content = EmptyBody() };
            ApplyArchonHeaders(tokenRequest, RequestKind.EmptyPost);
            var tokenResponse = await HttpClient.SendAsync(tokenRequest);
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
            // Advertise the version reported by the actual parser bundle (detected on
            // download) rather than a hardcoded constant, so this stays correct across
            // upstream parser bumps without a code change.
            ["parserVersion"] = plugin.ParserService.ParserVersion,
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

        Plugin.Log.Debug($"[CreateReport] POST /desktop-client/create-report: {json}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/desktop-client/create-report") { Content = JsonBody(json) };
        ApplyArchonHeaders(request, RequestKind.JsonApi);
        var response = await HttpClient.SendAsync(request);
        
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
        // 0. Make sure the parser bundle (and thus its parserVersion) is known before
        //    create-report advertises it. Cheap when the bundle is already cached.
        await plugin.ParserService.EnsureParserVersionAsync();

        // 1. Create report
        var reportCode = await CreateReportAsync(logPath, description, visibility, region, guildId);

        // 2. Process log with parser — returns one FightUpload per fight, each with a
        // slim master table scoped to that fight only (matches official client output).
        var uploads = await plugin.ParserService.ProcessLogAsync(logPath, reportCode, region);

        // 3. Upload each fight segment with its own master table.
        for (int i = 0; i < uploads.Count; i++)
        {
            int segmentId = i + 1;
            var u = uploads[i];
            await WithRetryAsync(() => UploadMasterTableAsync(reportCode, u.MasterTable, segmentId));
            await WithRetryAsync(() => UploadSegmentAsync(reportCode, u.Fight, segmentId, u.FightStartTime, u.FightEndTime));
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
                await plugin.ParserService.StartLiveLogAsync(logDirectory, region, uploadPreviousFights, async (masterData, fight, segmentId, startTime, endTime, inProgressEventCount) =>
                {
                    // Create report lazily on first fight (or first in-progress push)
                    if (reportCode == null)
                    {
                        Plugin.Log.Information("[LiveLog] First fight detected - creating report...");
                        reportCode = await CreateReportAsync("live.log", description, visibility, region, guildId);
                        CurrentReportCode = reportCode;

                        // Inform the parser so subsequent collect-master-info calls
                        // can be validated against the expected report code.
                        plugin.ParserService.SetReportCode(reportCode);
                    }

                    // A non-zero inProgressEventCount marks a provisional (real-time)
                    // segment for a fight that hasn't committed yet.
                    bool isRealTime = inProgressEventCount > 0;
                    await WithRetryAsync(() => UploadMasterTableAsync(reportCode, masterData, segmentId, isRealTime));
                    await WithRetryAsync(() => UploadSegmentAsync(reportCode, fight, segmentId, startTime, endTime, isLive: true, isRealTime: isRealTime, inProgressEventCount: inProgressEventCount));
                    // Only count a fight once it's finalized (not on provisional re-uploads).
                    if (inProgressEventCount == 0) LiveFightCount++;
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
        ApplyArchonHeaders(request, RequestKind.Multipart);

        var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        Plugin.Log.Debug($"Master table uploaded for {reportCode}");
    }

    private async Task UploadSegmentAsync(string reportCode, FightData fight, int segmentId, long startTime, long endTime, bool isLive = false, bool isRealTime = false, int inProgressEventCount = 0)
    {
        // Format events: header + count + events. Use the parser-provided eventCount
        // (numeric eventId delta, not line count) to match the official client exactly.
        var eventsContent = $"{fight.LogVersion}|{fight.GameVersion}\n{fight.EventCount}\n{fight.EventsString}";
        
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
            isRealTime,
            inProgressEventCount,
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
        ApplyArchonHeaders(request, RequestKind.Multipart);

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
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/desktop-client/terminate-report/{reportCode}") { Content = EmptyBody() };
                ApplyArchonHeaders(request, RequestKind.EmptyPost);
                var response = await HttpClient.SendAsync(request);
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
    public int EventCount { get; set; }
    public int LogVersion { get; set; } = 72;
    public int GameVersion { get; set; } = 1;
}

public class GuildInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
