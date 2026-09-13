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
    private int sortColumnIndex = 5; // Default sort by 解析百分比
    private bool sortAscending = false;

    public ParseViewerWindow(Plugin plugin)
        : base("解析查看器##FFLogsParseViewer", ImGuiWindowFlags.NoCollapse)
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
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "需要配置 FFLogs API");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "查看解析需要 FFLogs API 客户端。" +
            "请在 cn.fflogs.com/api/clients 免费创建，然后在下方输入凭据。");
        ImGui.Spacing();

        if (string.IsNullOrEmpty(apiClientId) && !string.IsNullOrEmpty(plugin.Configuration.ApiClientId))
            apiClientId = plugin.Configuration.ApiClientId;
        if (string.IsNullOrEmpty(apiClientSecret) && !string.IsNullOrEmpty(plugin.Configuration.ApiClientSecret))
            apiClientSecret = plugin.Configuration.ApiClientSecret;

        ImGui.Text("客户端 ID：");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##apiClientId", ref apiClientId, 256);

        ImGui.Spacing();
        ImGui.Text("客户端密钥：");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##apiClientSecret", ref apiClientSecret, 256, ImGuiInputTextFlags.Password);

        ImGui.Spacing();
        ImGui.Spacing();

        var canSave = !string.IsNullOrWhiteSpace(apiClientId) && !string.IsNullOrWhiteSpace(apiClientSecret);
        if (!canSave) ImGui.BeginDisabled();

        if (ImGui.Button("保存并连接", new Vector2(-1, 35)))
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
        ImGui.Text("报告：");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(150);
        ImGui.InputText("##reportCode", ref reportCode, 64);

        ImGui.SameLine();
        if (isLoadingFights)
        {
            ImGui.BeginDisabled();
            ImGui.Button("加载中...");
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button("获取"))
            {
                if (!string.IsNullOrWhiteSpace(reportCode))
                    _ = LoadFightsAsync();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("在 FFLogs 中打开") && !string.IsNullOrEmpty(reportCode))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"{plugin.FFLogsService.FflogsBaseUrl}/reports/{reportCode}",
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
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "正在加载战斗...");
            else if (!string.IsNullOrEmpty(reportCode) && errorMessage == null)
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "暂无战斗记录，点击“获取”加载。");
            return;
        }

        ImGui.Text("选择战斗：");

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
        if (ImGui.Begin($"解析 — {fightName}##ParseSidePanel", ref sidePanelOpen, ImGuiWindowFlags.NoCollapse))
        {
            if (isLoadingParses)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "正在加载解析...");
            }
            else if (parses.Count == 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f),
                    "暂无解析数据，报告可能仍在处理中。");
                if (ImGui.Button("刷新"))
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
        // Always show 6 columns: 名称, 职业, DPS, rDPS, aDPS, 解析百分比
        var tableFlags = ImGuiTableFlags.Borders
                       | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.SizingStretchProp
                       | ImGuiTableFlags.Sortable
                       | ImGuiTableFlags.ScrollY;

        if (ImGui.BeginTable("##ParsesTable", 6, tableFlags))
        {
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.None, 3.0f);
            ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.None, 1.5f);
            ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("rDPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("aDPS", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("解析百分比", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 1.0f);
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

                // 名称
                ImGui.TableNextColumn();
                ImGui.Text(parse.Name);
                if (!string.IsNullOrEmpty(parse.Server))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"({parse.Server})");
                }

                // 职业
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

                // 解析百分比
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
        // Columns: 0=名称, 1=职业, 2=DPS, 3=rDPS, 4=aDPS, 5=Parse%
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
                errorMessage = "未找到战斗，报告可能仍在处理中。";
            else if (exportedSegments == 0)
                errorMessage = "报告仍在处理，解析百分位数据尚未生成。";
            Plugin.Log.Information($"[ParseViewer] Loaded {fights.Count} fights (exported={exportedSegments})");
        }
        catch (Exception ex)
        {
            errorMessage = $"加载战斗失败：{ex.Message}";
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
            errorMessage = $"加载解析失败：{ex.Message}";
            Plugin.Log.Error(ex, "[ParseViewer] LoadParses failed");
        }
        finally
        {
            isLoadingParses = false;
        }
    }
}



