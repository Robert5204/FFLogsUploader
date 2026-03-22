using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FFLogsPlugin.Models;

namespace FFLogsPlugin.Services;

/// <summary>
/// Client for the FFLogs GraphQL v2 API using OAuth2 client credentials.
/// Used to fetch report data and fight rankings (parses).
/// </summary>
public class FFLogsApiService
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private readonly Plugin plugin;
    private readonly HttpClient httpClient;
    private string? accessToken;
    private DateTime tokenExpiry = DateTime.MinValue;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(plugin.Configuration.ApiClientId) &&
        !string.IsNullOrEmpty(plugin.Configuration.ApiClientSecret);

    public bool IsTokenValid => accessToken != null && DateTime.UtcNow < tokenExpiry;

    public FFLogsApiService(Plugin plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient();
    }

    public async Task<bool> EnsureTokenAsync()
    {
        if (IsTokenValid)
            return true;

        if (!IsConfigured)
        {
            Plugin.Log.Warning("[API] Client ID/Secret not configured.");
            return false;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = plugin.Configuration.ApiClientId,
                ["client_secret"] = plugin.Configuration.ApiClientSecret,
            };

            var response = await httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"[API] Token fetch failed: {response.StatusCode} - {body}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);

            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            Plugin.Log.Information("[API] Token acquired successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[API] Failed to fetch token.");
            return false;
        }
    }

    private async Task<JsonElement?> QueryAsync(string query)
    {
        if (!await EnsureTokenAsync())
            return null;

        try
        {
            var payload = JsonSerializer.Serialize(new { query });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(ApiUrl, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Plugin.Log.Error($"[API] Query failed: {response.StatusCode} - {body}");
                return null;
            }

            var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[API] Query exception.");
            return null;
        }
    }

    /// <summary>
    /// Fetches the list of fights for a report, with wall-clock times.
    /// </summary>
    public async Task<(List<ReportFight> Fights, int ExportedSegments)> FetchReportFightsAsync(string reportCode)
    {
        var fights = new List<ReportFight>();

        var query = $@"{{
            reportData {{
                report(code: ""{reportCode}"") {{
                    startTime
                    exportedSegments
                    fights {{
                        id
                        name
                        kill
                        bossPercentage
                        startTime
                        endTime
                        friendlyPlayers
                    }}
                }}
            }}
        }}";

        var result = await QueryAsync(query);
        if (result == null)
            return (fights, 0);

        int exportedSegments = 0;

        try
        {
            var report = result.Value
                .GetProperty("data")
                .GetProperty("reportData")
                .GetProperty("report");

            if (report.TryGetProperty("exportedSegments", out var es))
                exportedSegments = es.GetInt32();

            // Report start time — UNIX timestamp in ms
            long reportStartTime = 0;
            if (report.TryGetProperty("startTime", out var rst) && rst.ValueKind == JsonValueKind.Number)
                reportStartTime = (long)rst.GetDouble();

            var fightsArray = report.GetProperty("fights");

            foreach (var fight in fightsArray.EnumerateArray())
            {
                try
                {
                    var name = "";
                    if (fight.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                        name = nameProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(name) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) continue;

                    bool kill = false;
                    if (fight.TryGetProperty("kill", out var killProp) && killProp.ValueKind == JsonValueKind.True)
                        kill = true;

                    long fightStart = fight.TryGetProperty("startTime", out var s) && s.ValueKind == JsonValueKind.Number ? (long)s.GetDouble() : 0;
                    long fightEnd = fight.TryGetProperty("endTime", out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : 0;

                    // Compute wall-clock time: report start + fight offset
                    DateTimeOffset? wallClock = null;
                    if (reportStartTime > 0 && fightStart >= 0)
                    {
                        wallClock = DateTimeOffset.FromUnixTimeMilliseconds(reportStartTime + fightStart);
                    }

                    fights.Add(new ReportFight
                    {
                        Id = fight.GetProperty("id").GetInt32(),
                        Name = name,
                        Kill = kill,
                        BossPercentage = fight.TryGetProperty("bossPercentage", out var bp) && bp.ValueKind == JsonValueKind.Number
                            ? (float?)bp.GetDouble() : null,
                        StartTime = fightStart,
                        EndTime = fightEnd,
                        WallClockStart = wallClock,
                    });
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug($"[API] Skipping fight: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[API] Failed to parse fights response.");
        }

        Plugin.Log.Information($"[API] Report {reportCode}: {fights.Count} fights, exportedSegments={exportedSegments}");
        return (fights, exportedSegments);
    }

    /// <summary>
    /// Fetches parse data for a specific fight. Combines:
    /// - table(DamageDone) for real DPS (total/time)
    /// - rankings(playerMetric: rdps) for rDPS + parse %
    /// - rankings(playerMetric: dps) for aDPS
    /// All in a single GraphQL query.
    /// </summary>
    public async Task<List<FightParse>> FetchFightParsesAsync(string reportCode, int fightId)
    {
        var query = $@"{{
            reportData {{
                report(code: ""{reportCode}"") {{
                    damageTable: table(dataType: DamageDone, fightIDs: [{fightId}])
                    rdpsRankings: rankings(fightIDs: [{fightId}], playerMetric: rdps)
                    adpsRankings: rankings(fightIDs: [{fightId}], playerMetric: dps)
                    playerDetails(fightIDs: [{fightId}])
                }}
            }}
        }}";

        var result = await QueryAsync(query);
        if (result == null)
            return new List<FightParse>();

        try
        {
            var report = result.Value
                .GetProperty("data")
                .GetProperty("reportData")
                .GetProperty("report");

            // 1. Parse playerDetails for role info
            var playerRoles = new Dictionary<string, (string Role, string Spec)>();
            if (report.TryGetProperty("playerDetails", out var pd))
            {
                var pdData = pd.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(pd.GetString()!).RootElement : pd;
                if (pdData.TryGetProperty("data", out var pdInner) && pdInner.TryGetProperty("playerDetails", out var details))
                {
                    ParsePlayerDetailsRole(details, "tanks", "Tank", playerRoles);
                    ParsePlayerDetailsRole(details, "healers", "Healer", playerRoles);
                    ParsePlayerDetailsRole(details, "dps", "DPS", playerRoles);
                }
            }

            // 2. Parse rankings blobs
            var rdpsData = ParseRankingsBlob(report, "rdpsRankings");
            var adpsData = ParseRankingsBlob(report, "adpsRankings");

            // 3. Parse damage table for real DPS
            var dpsFromTable = new Dictionary<string, (float Dps, string Server, string Job)>();
            if (report.TryGetProperty("damageTable", out var dmgTable))
            {
                var dmgData = dmgTable.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(dmgTable.GetString()!).RootElement : dmgTable;

                if (dmgData.TryGetProperty("data", out var data) && data.TryGetProperty("entries", out var entries))
                {
                    var totalTime = data.TryGetProperty("totalTime", out var tt) && tt.ValueKind == JsonValueKind.Number
                        ? tt.GetDouble() : 1.0;

                    foreach (var entry in entries.EnumerateArray())
                    {
                        try
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(name)) continue;

                            var total = entry.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number
                                ? tot.GetDouble() : 0;
                            var dps = totalTime > 0 ? (float)(total / (totalTime / 1000.0)) : 0;
                            var server = entry.TryGetProperty("server", out var sv) ? sv.GetString() ?? "" : "";
                            var job = entry.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                            dpsFromTable[name] = (dps, server, job);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Debug($"[API] Skipping table entry: {ex.Message}");
                        }
                    }
                }
            }

            // 4. Merge everything: start with table entries (always available), layer in rankings
            var merged = new Dictionary<string, FightParse>();

            // Start with table data (real DPS)
            foreach (var (name, tableData) in dpsFromTable)
            {
                var role = "DPS";
                var spec = tableData.Job;
                if (playerRoles.TryGetValue(name, out var roleInfo))
                {
                    role = roleInfo.Role;
                    spec = roleInfo.Spec;
                }

                merged[name] = new FightParse
                {
                    Name = name,
                    Server = tableData.Server,
                    Job = tableData.Job,
                    Spec = spec,
                    Dps = tableData.Dps,
                    Role = role,
                };
            }

            // Layer in rDPS + parse %
            foreach (var (key, data) in rdpsData)
            {
                var name = data.Name;
                if (merged.TryGetValue(name, out var existing))
                {
                    existing.Rdps = data.Amount;
                    existing.ParsePercent = data.ParsePercent;
                    // Prefer server/job from rankings if table didn't have them
                    if (string.IsNullOrEmpty(existing.Server)) existing.Server = data.Server;
                }
                else
                {
                    var role = "DPS";
                    var spec = data.Spec;
                    if (playerRoles.TryGetValue(name, out var roleInfo))
                    {
                        role = roleInfo.Role;
                        spec = roleInfo.Spec;
                    }

                    merged[name] = new FightParse
                    {
                        Name = name,
                        Server = data.Server,
                        Job = data.Job,
                        Spec = spec,
                        Rdps = data.Amount,
                        ParsePercent = data.ParsePercent,
                        Role = string.IsNullOrEmpty(role) ? data.Role : role,
                    };
                }
            }

            // Layer in aDPS
            foreach (var (key, data) in adpsData)
            {
                var name = data.Name;
                if (merged.TryGetValue(name, out var existing))
                {
                    existing.Adps = data.Amount;
                    existing.ParsePercent ??= data.ParsePercent;
                }
                else
                {
                    merged[name] = new FightParse
                    {
                        Name = name,
                        Server = data.Server,
                        Job = data.Job,
                        Spec = data.Spec,
                        Adps = data.Amount,
                        ParsePercent = data.ParsePercent,
                        Role = data.Role,
                    };
                }
            }

            var parses = merged.Values
                .Where(p => p.Name != "Limit Break") // Skip LB
                .ToList();

            Plugin.Log.Information($"[API] Loaded {parses.Count} entries for fight {fightId} " +
                $"(table={dpsFromTable.Count}, rdps={rdpsData.Count}, adps={adpsData.Count})");
            return parses;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[API] Failed to parse fight data.");
            return new List<FightParse>();
        }
    }

    // ------ Ranking blob parsing helpers ------

    private class RankingEntry
    {
        public string Name { get; set; } = "";
        public string Server { get; set; } = "";
        public string Job { get; set; } = "";
        public string Spec { get; set; } = "";
        public float? ParsePercent { get; set; }
        public float? Amount { get; set; }
        public string Role { get; set; } = "";
    }

    private Dictionary<string, RankingEntry> ParseRankingsBlob(JsonElement report, string fieldName)
    {
        var entries = new Dictionary<string, RankingEntry>();

        if (!report.TryGetProperty(fieldName, out var rankings))
            return entries;

        JsonElement rankingsData;
        if (rankings.ValueKind == JsonValueKind.String)
        {
            var innerDoc = JsonDocument.Parse(rankings.GetString()!);
            rankingsData = innerDoc.RootElement;
        }
        else
        {
            rankingsData = rankings;
        }

        if (!rankingsData.TryGetProperty("data", out var dataArray) || dataArray.GetArrayLength() == 0)
            return entries;

        foreach (var fight in dataArray.EnumerateArray())
        {
            if (!fight.TryGetProperty("roles", out var roles))
                continue;

            ParseRoleIntoDict(roles, "tanks", "Tank", entries);
            ParseRoleIntoDict(roles, "healers", "Healer", entries);
            ParseRoleIntoDict(roles, "dps", "DPS", entries);
        }

        return entries;
    }

    private void ParseRoleIntoDict(JsonElement roles, string roleName, string roleLabel, Dictionary<string, RankingEntry> entries)
    {
        if (roles.ValueKind != JsonValueKind.Object)
            return;

        if (!roles.TryGetProperty(roleName, out var role))
            return;
        if (!role.TryGetProperty("characters", out var characters))
            return;

        foreach (var character in characters.EnumerateArray())
        {
            try
            {
                var name = character.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;

                var serverName = "";
                if (character.TryGetProperty("server", out var serverProp))
                {
                    if (serverProp.ValueKind == JsonValueKind.String)
                        serverName = serverProp.GetString() ?? "";
                    else if (serverProp.ValueKind == JsonValueKind.Object && serverProp.TryGetProperty("name", out var sn))
                        serverName = sn.GetString() ?? "";
                }

                entries[name] = new RankingEntry
                {
                    Name = name,
                    Server = serverName,
                    Job = character.TryGetProperty("class", out var c) ? c.GetString() ?? "" : "",
                    Spec = character.TryGetProperty("spec", out var sp) ? sp.GetString() ?? "" : "",
                    ParsePercent = character.TryGetProperty("rankPercent", out var rp) && rp.ValueKind == JsonValueKind.Number
                        ? (float?)rp.GetDouble() : null,
                    Amount = character.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number
                        ? (float?)a.GetDouble() : null,
                    Role = roleLabel,
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"[API] Skipping character: {ex.Message}");
            }
        }
    }

    private void ParsePlayerDetailsRole(JsonElement details, string roleName, string roleLabel, Dictionary<string, (string Role, string Spec)> playerRoles)
    {
        if (details.ValueKind != JsonValueKind.Object)
            return;

        if (!details.TryGetProperty(roleName, out var rolePlayers))
            return;

        foreach (var player in rolePlayers.EnumerateArray())
        {
            var name = player.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var spec = player.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(name))
                playerRoles[name] = (roleLabel, spec);
        }
    }
}
