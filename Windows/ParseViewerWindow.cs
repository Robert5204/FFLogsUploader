using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFLogsPlugin.Models;
using FFLogsPlugin.Services;

namespace FFLogsPlugin.Windows;

public class ParseViewerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // API credential input (shown inline when not configured)
    private string apiClientId = string.Empty;
    private string apiClientSecret = string.Empty;

    // Report state
    private string reportCode = string.Empty;
    private List<ReportFight> fights = new();
    private List<FightParse> parses = new();
    private int selectedFightIndex = -1;

    // Async state
    private bool isLoadingFights = false;
    private bool isLoadingParses = false;
    private string? errorMessage = null;

    // Sort state
    private int sortColumnIndex = 5; // Default sort by Parse %
    private bool sortAscending = false;

    public ParseViewerWindow(Plugin plugin)
        : base("Parse Viewer##FFLogsParseViewer", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 400),
            MaximumSize = new Vector2(500, 800)
        };
    }

    public void Dispose() { }

    /// <summary>
    /// Opens the window with a specific report code and auto-fetches fights.
    /// </summary>
    public void OpenWithReport(string code)
    {
        reportCode = code;
        fights.Clear();
        parses.Clear();
        selectedFightIndex = -1;
        errorMessage = null;
        IsOpen = true;

        if (plugin.FFLogsApiService.IsConfigured)
        {
            _ = LoadFightsAsync();
        }
    }

    public override void Draw()
    {
        if (!plugin.FFLogsApiService.IsConfigured)
        {
            DrawApiSetup();
            return;
        }

        DrawReportHeader();
        ImGui.Separator();
        ImGui.Spacing();
        DrawFightSelector();
        DrawError();

    }

    public override void PostDraw()
    {
        // Draw parses in a separate side panel (must be done in PostDraw so it renders OUTSIDE the main window's ImGui.Begin/End block)
        if (plugin.FFLogsApiService.IsConfigured && selectedFightIndex >= 0 && selectedFightIndex < fights.Count)
        {
            DrawParseSidePanel();
        }
    }

    private void DrawApiSetup()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "FFLogs API Setup Required");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "To view parses, you need an FFLogs API client. " +
            "Create one at fflogs.com/api/clients (free), then enter the credentials below.");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(apiClientId) && !string.IsNullOrEmpty(plugin.Configuration.ApiClientId))
            apiClientId = plugin.Configuration.ApiClientId;
        if (string.IsNullOrEmpty(apiClientSecret) && !string.IsNullOrEmpty(plugin.Configuration.ApiClientSecret))
            apiClientSecret = plugin.Configuration.ApiClientSecret;

        ImGui.Text("Client ID:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##apiClientId", ref apiClientId, 256);

        ImGui.Spacing();
        ImGui.Text("Client Secret:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##apiClientSecret", ref apiClientSecret, 256, ImGuiInputTextFlags.Password);

        ImGui.Spacing();
        ImGui.Spacing();

        var canSave = !string.IsNullOrWhiteSpace(apiClientId) && !string.IsNullOrWhiteSpace(apiClientSecret);
        if (!canSave) ImGui.BeginDisabled();

        if (ImGui.Button("Save & Connect", new Vector2(-1, 35)))
        {
            plugin.Configuration.ApiClientId = apiClientId.Trim();
            plugin.Configuration.ApiClientSecret = apiClientSecret.Trim();
            plugin.Configuration.Save();
            Plugin.Log.Information("[ParseViewer] API credentials saved.");

            if (!string.IsNullOrEmpty(reportCode))
            {
                _ = LoadFightsAsync();
            }
        }

        if (!canSave) ImGui.EndDisabled();
        DrawError();
    }

    private void DrawReportHeader()
    {
        ImGui.Text("Report:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(150);
        ImGui.InputText("##reportCode", ref reportCode, 64);

        ImGui.SameLine();
        if (isLoadingFights)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Loading...");
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button("Fetch"))
            {
                if (!string.IsNullOrWhiteSpace(reportCode))
                    _ = LoadFightsAsync();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Open on FFLogs") && !string.IsNullOrEmpty(reportCode))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.fflogs.com/reports/{reportCode}",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[ParseViewer] Failed to open URL: {ex.Message}");
            }
        }
    }

    private void DrawFightSelector()
    {
        if (fights.Count == 0)
        {
            if (isLoadingFights)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Loading fights...");
            else if (!string.IsNullOrEmpty(reportCode) && errorMessage == null)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No fights loaded. Click Fetch to load.");
            return;
        }

        ImGui.Text("Select a fight:");

        var availH = ImGui.GetContentRegionAvail().Y - 25;
        var listHeight = Math.Max(100, Math.Min(availH, fights.Count * ImGui.GetTextLineHeightWithSpacing() + 8));
        if (ImGui.BeginChild("##FightList", new Vector2(-1, listHeight), true))
        {
            for (int i = 0; i < fights.Count; i++)
            {
                var fight = fights[i];
                var isSelected = (selectedFightIndex == i);

                var color = fight.Kill
                    ? new Vector4(0.3f, 0.9f, 0.3f, 1.0f)
                    : new Vector4(0.9f, 0.4f, 0.4f, 1.0f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                if (ImGui.Selectable($"{fight.DisplayString}##fight{i}", isSelected))
                {
                    selectedFightIndex = i;
                    _ = LoadParsesAsync(fight.Id);
                }

                ImGui.PopStyleColor();
            }
        }
        ImGui.EndChild();
    }

    private void DrawParseSidePanel()
    {
        var mainPos = ImGui.GetWindowPos();
        var mainSize = ImGui.GetWindowSize();

        ImGui.SetNextWindowPos(new Vector2(mainPos.X + mainSize.X + 5, mainPos.Y), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(650, 300), new Vector2(950, 800));

        var fightName = fights[selectedFightIndex].DisplayString;
        var sidePanelOpen = true;
        if (ImGui.Begin($"Parses — {fightName}##ParseSidePanel", ref sidePanelOpen, ImGuiWindowFlags.NoCollapse))
        {
            if (isLoadingParses)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Loading parses...");
            }
            else if (parses.Count == 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f),
                    "No parse data yet. Report may still be processing.");
                if (ImGui.Button("Refresh"))
                    _ = LoadParsesAsync(fights[selectedFightIndex].Id);
            }
            else
            {
                DrawParsesTable();
            }
        }
        ImGui.End();

        if (!sidePanelOpen)
            selectedFightIndex = -1;
    }

    private void DrawParsesTable()
    {
        // Always show 6 columns: Name, Job, DPS, rDPS, aDPS, Parse %
        var tableFlags = ImGuiTableFlags.Borders
                       | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.SizingStretchProp
                       | ImGuiTableFlags.Sortable
                       | ImGuiTableFlags.ScrollY;

        if (ImGui.BeginTable("##ParsesTable", 6, tableFlags))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 3.0f);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.None, 1.5f);
            ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("aDPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("Parse %", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 1.0f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Handle sorting
            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty)
            {
                var spec = sortSpecs.Specs;
                sortColumnIndex = spec.ColumnIndex;
                sortAscending = (spec.SortDirection == ImGuiSortDirection.Ascending);
                SortParses();
                sortSpecs.SpecsDirty = false;
            }

            foreach (var parse in parses)
            {
                ImGui.TableNextRow();

                // Name
                ImGui.TableNextColumn();
                ImGui.Text(parse.Name);
                if (!string.IsNullOrEmpty(parse.Server))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"({parse.Server})");
                }

                // Job
                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(parse.Spec) ? parse.Job : parse.Spec);

                // DPS
                ImGui.TableNextColumn();
                DrawNumberCell(parse.Dps);

                // rDPS
                ImGui.TableNextColumn();
                DrawNumberCell(parse.Rdps);

                // aDPS
                ImGui.TableNextColumn();
                DrawNumberCell(parse.Adps);

                // Parse %
                ImGui.TableNextColumn();
                if (parse.ParsePercent.HasValue)
                {
                    var color = GetPercentileColor(parse.ParsePercent.Value);
                    ImGui.TextColored(color, $"{parse.ParsePercent.Value:F0}");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "-");
                }
            }

            ImGui.EndTable();
        }
    }

    private static void DrawNumberCell(float? value)
    {
        if (value.HasValue)
            ImGui.Text($"{value.Value:N1}");
        else
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "-");
    }

    private void SortParses()
    {
        // Columns: 0=Name, 1=Job, 2=DPS, 3=rDPS, 4=aDPS, 5=Parse%
        Func<FightParse, IComparable> keySelector = sortColumnIndex switch
        {
            0 => p => p.Name,
            1 => p => string.IsNullOrEmpty(p.Spec) ? p.Job : p.Spec,
            2 => p => p.Dps ?? -1f,
            3 => p => p.Rdps ?? -1f,
            4 => p => p.Adps ?? -1f,
            5 => p => p.ParsePercent ?? -1f,
            _ => p => p.Name,
        };

        if (sortAscending)
            parses = parses.OrderBy(keySelector).ToList();
        else
            parses = parses.OrderByDescending(keySelector).ToList();
    }

    private void DrawError()
    {
        if (!string.IsNullOrEmpty(errorMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), errorMessage);
        }
    }

    private static Vector4 GetPercentileColor(float percentile)
    {
        return percentile switch
        {
            >= 100 => new Vector4(0.894f, 0.647f, 0.282f, 1.0f),  // Gold
            >= 99  => new Vector4(0.894f, 0.647f, 0.282f, 1.0f),  // Gold
            >= 95  => new Vector4(1.0f, 0.502f, 0.0f, 1.0f),      // Orange
            >= 75  => new Vector4(0.639f, 0.208f, 0.933f, 1.0f),   // Purple
            >= 50  => new Vector4(0.0f, 0.439f, 1.0f, 1.0f),       // Blue
            >= 25  => new Vector4(0.118f, 0.588f, 0.0f, 1.0f),     // Green
            _      => new Vector4(0.6f, 0.6f, 0.6f, 1.0f),         // Gray
        };
    }

    private async System.Threading.Tasks.Task LoadFightsAsync()
    {
        isLoadingFights = true;
        errorMessage = null;
        fights.Clear();
        parses.Clear();
        selectedFightIndex = -1;

        try
        {
            var (loadedFights, exportedSegments) = await plugin.FFLogsApiService.FetchReportFightsAsync(reportCode);
            fights = loadedFights;
            if (fights.Count == 0)
                errorMessage = "No fights found. The report may still be processing.";
            else if (exportedSegments == 0)
                errorMessage = "Report is still being processed. Parses may not have percentiles yet.";
            Plugin.Log.Information($"[ParseViewer] Loaded {fights.Count} fights (exported={exportedSegments})");
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to load fights: {ex.Message}";
            Plugin.Log.Error(ex, "[ParseViewer] LoadFights failed");
        }
        finally
        {
            isLoadingFights = false;
        }
    }

    private async System.Threading.Tasks.Task LoadParsesAsync(int fightId)
    {
        isLoadingParses = true;
        errorMessage = null;
        parses.Clear();

        try
        {
            parses = await plugin.FFLogsApiService.FetchFightParsesAsync(reportCode, fightId);
            SortParses();
            Plugin.Log.Information($"[ParseViewer] Loaded {parses.Count} parses for fight {fightId}");
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to load parses: {ex.Message}";
            Plugin.Log.Error(ex, "[ParseViewer] LoadParses failed");
        }
        finally
        {
            isLoadingParses = false;
        }
    }
}
