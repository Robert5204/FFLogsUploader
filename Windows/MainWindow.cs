using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FFLogsPlugin.Services;

namespace FFLogsPlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private FileDialog? fileDialog;
    private bool isSelectingFolder = false;

    private string logPath = string.Empty;
    private int selectedGuildIndex = 0;
    private int selectedRegion = 0;
    private int selectedVisibility = 1;
    private string description = string.Empty;
    private bool includeEntireFile = true;

    private string statusMessage = string.Empty;
    private bool isProcessing = false;

    private readonly string[] regions = { "NA", "EU", "JP", "CN", "KR" };
    private readonly string[] visibilities = { "Public", "Private", "Unlisted" };

    public MainWindow(Plugin plugin)
        : base("FFLogs Uploader##FFLogsMain", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 400),
            MaximumSize = new Vector2(800, 700)
        };

        logPath = plugin.Configuration.LogDirectory;
        selectedRegion = Math.Max(0, plugin.Configuration.Region - 1);
        selectedVisibility = plugin.Configuration.Visibility;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Handle file dialog
        if (fileDialog != null)
        {
            if (fileDialog.Draw())
            {
                if (fileDialog.GetIsOk())
                {
                    var results = fileDialog.GetResults();
                    if (results.Count > 0)
                    {
                        logPath = isSelectingFolder ? fileDialog.GetCurrentPath() : results[0];
                    }
                }
                fileDialog = null;
            }
        }
        
        DrawHeader();
        ImGui.Separator();

        if (ImGui.BeginTabBar("FFLogsTabs"))
        {
            if (ImGui.BeginTabItem("Live Log"))
            {
                DrawLiveLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Upload a Log"))
            {
                DrawUploadTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawStatus();
    }

    private void DrawHeader()
    {
        // Show username from FFLogsService, not email
        var displayName = plugin.FFLogsService.Username ?? plugin.Configuration.Email ?? "Unknown";
        ImGui.Text($"Logged in as: {displayName}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 80);
        if (ImGui.Button("Log Out"))
        {
            plugin.FFLogsService.Logout();
            plugin.ShowLoginWindow();
        }
    }

    private void DrawLiveLogTab()
    {
        ImGui.Spacing();
        ImGui.Text("Choose the directory that the ACT FFXIV plug-in writes logs to:");
        ImGui.SetNextItemWidth(-80);
        
        // Disable path input when live logging
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.BeginDisabled();
        
        ImGui.InputText("##logdir", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse##dir"))
        {
            isSelectingFolder = true;
            fileDialog = new FileDialog(
                "SelectLogFolder",
                "Select Log Folder",
                string.Empty,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                string.Empty,
                string.Empty,
                1,
                false,
                ImGuiFileDialogFlags.SelectOnly
            );
            fileDialog.Show();
        }
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.EndDisabled();

        ImGui.Spacing();
        
        // Disable options when live logging
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.BeginDisabled();
            
        DrawSharedOptions();
        ImGui.Spacing();

        ImGui.Checkbox("Include the Entire File in the Report", ref includeEntireFile);
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.EndDisabled();
            
        ImGui.Spacing();

        // Show Start or Stop button based on state
        if (plugin.FFLogsService.IsLiveLogging)
        {
            // Show status
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), 
                $"● Live logging active - {plugin.FFLogsService.LiveFightCount} fight(s) uploaded");
            ImGui.Text($"Report: {plugin.FFLogsService.CurrentReportCode ?? "unknown"}");
            ImGui.Spacing();
            
            // Red stop button
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1.0f));
            
            if (ImGui.Button("Stop Live Logging", new Vector2(-1, 40)))
            {
                plugin.FFLogsService.StopLiveLog();
            }
            
            ImGui.PopStyleColor(3);
        }
        else
        {
            // Green start button - full width
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.6f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.7f, 0.1f, 1.0f));
            
            if (ImGui.Button("Start Live Logging", new Vector2(-1, 40)))
            {
                StartLiveLog();
            }
            
            ImGui.PopStyleColor(3);
        }
    }

    private void DrawUploadTab()
    {
        ImGui.Spacing();
        ImGui.Text("Choose the file you want to upload:");
        ImGui.SetNextItemWidth(-80);
        ImGui.InputText("##logfile", ref logPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse##file"))
        {
            isSelectingFolder = false;
            fileDialog = new FileDialog(
                "SelectLogFile",
                "Select Log File",
                ".log,.txt",
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                string.Empty,
                string.Empty,
                1,
                false,
                ImGuiFileDialogFlags.SelectOnly
            );
            fileDialog.Show();
        }

        ImGui.Spacing();
        DrawSharedOptions();
        ImGui.Spacing();

        DrawGoButton("Go!", StartUpload);
    }

    private void DrawSharedOptions()
    {
        ImGui.Text("Choose the guild you want to upload to:");
        
        // Build guild list from service
        var guildList = new List<(string? Id, string Name)> { (null, "Personal Logs") };
        foreach (var guild in plugin.FFLogsService.Guilds)
        {
            guildList.Add((guild.Id, guild.Name));
        }
        
        var guildNames = new string[guildList.Count];
        for (int i = 0; i < guildList.Count; i++)
            guildNames[i] = guildList[i].Name;
        
        // Clamp selectedGuildIndex in case guilds changed
        if (selectedGuildIndex >= guildList.Count)
            selectedGuildIndex = 0;
        
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##guild", ref selectedGuildIndex, guildNames, guildNames.Length);
        ImGui.SameLine();

        ImGui.SetNextItemWidth(60);
        ImGui.Combo("##region", ref selectedRegion, regions, regions.Length);
        ImGui.SameLine();

        ImGui.SetNextItemWidth(80);
        ImGui.Combo("##visibility", ref selectedVisibility, visibilities, visibilities.Length);

        ImGui.Spacing();
        ImGui.Text("Enter a description for the report:");
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

    private void DrawGoButton(string label, Action action)
    {
        if (isProcessing) ImGui.BeginDisabled();

        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 115);
        if (ImGui.Button(isProcessing ? "Processing..." : label, new Vector2(100, 30)))
            action();

        if (isProcessing) ImGui.EndDisabled();
    }

    private void DrawStatus()
    {
        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Separator();
            ImGui.TextWrapped(statusMessage);
        }
    }

    private void StartLiveLog()
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = "Please enter a log directory path first.";
            return;
        }

        SaveConfig();
        statusMessage = ""; // Clear status - UI will show live logging status instead
        _ = DoLiveLog();
    }

    private async System.Threading.Tasks.Task DoLiveLog()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            await plugin.FFLogsService.StartLiveLogAsync(logPath, selectedRegion + 1, selectedVisibility, guildId, description);
            // Don't set status - background task is now running
        }
        catch (Exception ex)
        {
            statusMessage = $"Error: {ex.Message}";
            Plugin.Log.Error(ex, "Live log failed");
        }
    }

    private void StartUpload()
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            statusMessage = "Please select a log file first.";
            return;
        }

        SaveConfig();
        statusMessage = "Uploading...";
        isProcessing = true;
        _ = DoUpload();
    }

    private async System.Threading.Tasks.Task DoUpload()
    {
        try
        {
            var guildId = GetSelectedGuildId();
            var reportCode = await plugin.FFLogsService.UploadLogAsync(logPath, selectedRegion + 1, selectedVisibility, guildId, description);
            statusMessage = $"Upload complete! Report: {reportCode}";
        }
        catch (Exception ex)
        {
            statusMessage = $"Error: {ex.Message}";
            Plugin.Log.Error(ex, "Upload failed");
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void SaveConfig()
    {
        plugin.Configuration.LogDirectory = System.IO.Path.GetDirectoryName(logPath) ?? logPath;
        plugin.Configuration.Region = selectedRegion + 1;
        plugin.Configuration.Visibility = selectedVisibility;
        plugin.Configuration.Save();
    }
}
