using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FFLogsPlugin.Helpers;
using FFLogsPlugin.Models;
using FFLogsPlugin.Services;

namespace FFLogsPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly FileDialogManager fileDialogManager = new();

    private string logPath = string.Empty;
    private int selectedGuildIndex = 0;
    private int selectedRegion = 0;
    private int selectedVisibility = 1;
    private string description = string.Empty;
    private bool uploadPreviousFights = true;

    private string statusMessage = string.Empty;
    private bool isProcessing = false;

    private readonly string[] visibilities = { "公开", "私有", "不公开列出" };

    public MainWindow(Plugin plugin)
        : base("FFLogs 日志上传器##FFLogsMain", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(800, 700)
        };

        logPath = plugin.Configuration.LogDirectory;
        
        // Auto-detect if empty OR if saved path is invalid
        if (string.IsNullOrEmpty(logPath) || !LogFileHelper.IsValidLogPath(logPath))
        {
            logPath = LogFileHelper.AutoDetectLogDirectory();
            if (!string.IsNullOrEmpty(logPath))
            {
                plugin.Configuration.LogDirectory = logPath;
                plugin.Configuration.Save();
            }
        }

        // Auto-populate with the latest log file for easier manual upload
        if (!string.IsNullOrEmpty(logPath) && System.IO.Directory.Exists(logPath))
        {
            var latestFile = LogFileHelper.GetLatestLogFileFromPath(logPath);
            if (latestFile != logPath)
            {
                logPath = latestFile;
            }
        }

        selectedRegion = FFLogsRegionExtensions.ApiValueToComboIndex(plugin.Configuration.Region);
        selectedVisibility = plugin.Configuration.Visibility;
    }

    public void Dispose() { }

    public override void Draw()
    {
        fileDialogManager.Draw();

        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("FFLogsTabs"))
        {
            if (ImGui.BeginTabItem("实时记录"))
            {
                DrawLiveLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("上传日志"))
            {
                DrawUploadTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        var displayName = plugin.FFLogsService.Username ?? plugin.Configuration.Email ?? "未知";
        ImGui.Text($"当前账号：{displayName}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 80);
        if (ImGui.Button("退出登录"))
        {
            plugin.FFLogsService.Logout();
            plugin.ShowLoginWindow();
        }
    }

    private void DrawLiveLogTab()
    {
        ImGui.Spacing();
        ImGui.Text("选择 ACT FFXIV 插件写入日志的目录：");
        ImGui.SetNextItemWidth(-80);
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.BeginDisabled();
        
        ImGui.InputText("##logdir", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("浏览##dir"))
        {
            fileDialogManager.OpenFolderDialog(
                "选择日志目录",
                (success, path) =>
                {
                    if (success && !string.IsNullOrEmpty(path))
                        logPath = path;
                },
                GetDialogStartPath());
        }

        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.EndDisabled();

        ImGui.Spacing();
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.BeginDisabled();
            
        DrawSharedOptions();
        ImGui.Spacing();

        ImGui.Checkbox("将整个文件包含在报告中", ref uploadPreviousFights);
        var autoStartLiveLogging = plugin.Configuration.AutoStartLiveLogging;
        if (ImGui.Checkbox(Loc.T("Start live logging automatically after login", "登录后自动开始实时上传"), ref autoStartLiveLogging))
        {
            plugin.Configuration.AutoStartLiveLogging = autoStartLiveLogging;
            plugin.Configuration.Save();
        }
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.EndDisabled();
            
        ImGui.Spacing();

        if (plugin.FFLogsService.IsLiveLogging)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), 
                $"● Live logging active - {plugin.FFLogsService.LiveFightCount} fight(s) uploaded");
            ImGui.Text($"报告：{plugin.FFLogsService.CurrentReportCode ?? "未知"}");
            ImGui.Spacing();
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1.0f));
            
            if (ImGui.Button("停止实时记录", new Vector2(-1, 40)))
            {
                plugin.FFLogsService.StopLiveLog();
            }
            
            ImGui.PopStyleColor(3);
            
            if (plugin.FFLogsService.LiveFightCount > 0 && !string.IsNullOrEmpty(plugin.FFLogsService.CurrentReportCode))
            {
                ImGui.Spacing();
                if (ImGui.Button("查看解析", new Vector2(-1, 30)))
                {
                    plugin.ShowParseViewer(plugin.FFLogsService.CurrentReportCode);
                }
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.7f, 0.1f, 1.0f));
            
            if (ImGui.Button("开始实时记录", new Vector2(-1, 40)))
            {
                StartLiveLog();
            }
            
            ImGui.PopStyleColor(3);
        }
    }

    private void DrawUploadTab()
    {
        ImGui.Spacing();
        ImGui.Text("选择要上传的文件：");
        ImGui.SetNextItemWidth(-80);
        ImGui.InputText("##logfile", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("浏览##file"))
        {
            fileDialogManager.OpenFileDialog(
                "选择日志文件",
                "日志文件{.log},所有文件{.*}",
                (success, paths) =>
                {
                    if (success && paths.Count > 0)
                        logPath = paths[0];
                },
                1,
                GetDialogStartPath());
        }

        ImGui.Spacing();
        DrawSharedOptions();
        ImGui.Spacing();

        DrawGoButton("开始", StartUpload);

        DrawStatus();
    }

    private void DrawSharedOptions()
    {
        ImGui.Text("选择上传目标公会：");
        
        var guildList = new List<(string? Id, string Name)> { (null, "个人日志") };
        foreach (var guild in plugin.FFLogsService.Guilds)
        {
            guildList.Add((guild.Id, guild.Name));
        }
        
        var guildNames = new string[guildList.Count];
        for (int i = 0; i < guildList.Count; i++)
            guildNames[i] = guildList[i].Name;
        
        if (selectedGuildIndex >= guildList.Count)
            selectedGuildIndex = 0;
        
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##guild", ref selectedGuildIndex, guildNames, guildNames.Length);
        ImGui.SameLine();

        if (selectedGuildIndex == 0)
        {
            ImGui.SetNextItemWidth(60);
            ImGui.Combo("##region", ref selectedRegion, FFLogsRegionExtensions.DisplayNames, FFLogsRegionExtensions.DisplayNames.Length);
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(80);
        ImGui.Combo("##visibility", ref selectedVisibility, visibilities, visibilities.Length);

        ImGui.Spacing();
        ImGui.Text("输入报告描述：");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##description", ref description, 256);
    }

    private string? GetSelectedGuildId()
    {
        if (selectedGuildIndex <= 0)
            return null;
        
        var guilds = plugin.FFLogsService.Guilds;
        if (selectedGuildIndex - 1 < guilds.Count)
            return guilds[selectedGuildIndex - 1].Id;
        
        return null;
    }

    /// <summary>
    /// Returns the best starting directory for the file dialog based on the current logPath.
    /// </summary>
    private string GetDialogStartPath()
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            // If logPath is a file, use its directory
            if (System.IO.File.Exists(logPath))
            {
                var dir = System.IO.Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
            // If logPath is a directory, use it directly
            if (System.IO.Directory.Exists(logPath))
                return logPath;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void DrawGoButton(string label, Action action)
    {
        if (isProcessing) ImGui.BeginDisabled();

        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 115);
        if (ImGui.Button(isProcessing ? "处理中..." : label, new Vector2(100, 30)))
            action();

        if (isProcessing) ImGui.EndDisabled();
    }

    private void DrawStatus()
    {
        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Separator();
            ImGui.TextWrapped(statusMessage);

            // Show 查看解析 button when upload is complete
            if (statusMessage.StartsWith("上传完成！") && plugin.FFLogsService.RecentReportCodes.Count > 0)
            {
                ImGui.Spacing();
                if (ImGui.Button("查看解析##status", new Vector2(-1, 30)))
                {
                    plugin.ShowParseViewer(plugin.FFLogsService.RecentReportCodes[0]);
                }
            }
        }
    }

    private void StartLiveLog()
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = "请先输入日志目录路径。";
            return;
        }

        var effectivePath = LogFileHelper.GetDirectoryFromPath(logPath);
        if (effectivePath != logPath)
        {
            logPath = effectivePath;
        }

        SaveConfig();
        statusMessage = "";
        _ = DoLiveLog();
    }

    private async System.Threading.Tasks.Task DoLiveLog()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            await plugin.FFLogsService.StartLiveLogAsync(logPath, FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion), selectedVisibility, guildId, description, uploadPreviousFights);
        }
        catch (Exception ex)
        {
            statusMessage = $"错误：{ex.Message}";
            Plugin.Log.Error(ex, "Live log failed");
        }
    }

    private void StartUpload()
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = "请先选择日志文件。";
            return;
        }

        var effectivePath = LogFileHelper.GetLatestLogFileFromPath(logPath);
        if (effectivePath != logPath)
        {
            logPath = effectivePath;
        }

        if (!System.IO.File.Exists(logPath))
        {
            statusMessage = "指定目录中未找到日志文件。";
            return;
        }

        SaveConfig();
        statusMessage = "上传中...";
        isProcessing = true;
        _ = DoUpload();
    }

    private async System.Threading.Tasks.Task DoUpload()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            var reportCode = await plugin.FFLogsService.UploadLogAsync(logPath, FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion), selectedVisibility, guildId, description);
            statusMessage = $"上传完成！ Report: {reportCode}";
        }
        catch (Exception ex)
        {
            statusMessage = $"错误：{ex.Message}";
            Plugin.Log.Error(ex, "Upload failed");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void SaveConfig()
    {
        var directoryToSave = logPath;
        if (System.IO.File.Exists(logPath))
        {
            directoryToSave = System.IO.Path.GetDirectoryName(logPath) ?? logPath;
        }
        
        plugin.Configuration.LogDirectory = directoryToSave;
        plugin.Configuration.Region = FFLogsRegionExtensions.ComboIndexToApiValue(selectedRegion);
        plugin.Configuration.Visibility = selectedVisibility;
        plugin.Configuration.Save();
    }
}


