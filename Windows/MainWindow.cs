using System;
using System.Collections.Generic;
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
    private FileDialog? fileDialog;
    private bool isSelectingFolder = false;

    private string logPath = string.Empty;
    private int selectedGuildIndex = 0;
    private int selectedRegion = 0;
    private int selectedVisibility = 1;
    private string description = string.Empty;
    private bool uploadPreviousFights = true;

    private string statusMessage = string.Empty;
    private bool isProcessing = false;

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

        selectedRegion = Math.Max(0, plugin.Configuration.Region - 1);
        selectedVisibility = plugin.Configuration.Visibility;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Handle file dialog
        if (fileDialog != null)
        {
            try
            {
                if (fileDialog.Draw())
                {
                    if (fileDialog.GetIsOk())
                    {
                        var results = fileDialog.GetResults();
                        if (results != null && results.Count > 0)
                        {
                            logPath = isSelectingFolder ? fileDialog.GetCurrentPath() : results[0];
                        }
                    }
                    fileDialog = null;
                }
            }
            catch (NullReferenceException)
            {
                // Dalamud's ImGuiFileDialog throws NullReferenceException in
                // AddFileNameInSelection when double-clicking a file. The selection
                // path is still valid at this point, so extract it and close the dialog.
                try
                {
                    var currentPath = fileDialog!.GetCurrentPath();
                    var results = fileDialog.GetResults();
                    if (results != null && results.Count > 0)
                    {
                        logPath = isSelectingFolder ? currentPath : results[0];
                    }
                    else if (!string.IsNullOrEmpty(currentPath))
                    {
                        logPath = currentPath;
                    }
                }
                catch
                {
                    // If even recovery fails, just close the dialog silently
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
    }

    private void DrawHeader()
    {
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
                GetDialogStartPath(),
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
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.BeginDisabled();
            
        DrawSharedOptions();
        ImGui.Spacing();

        ImGui.Checkbox("Include the Entire File in the Report", ref uploadPreviousFights);
        
        if (plugin.FFLogsService.IsLiveLogging)
            ImGui.EndDisabled();
            
        ImGui.Spacing();

        if (plugin.FFLogsService.IsLiveLogging)
        {
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1.0f), 
                $"● Live logging active - {plugin.FFLogsService.LiveFightCount} fight(s) uploaded");
            ImGui.Text($"Report: {plugin.FFLogsService.CurrentReportCode ?? "unknown"}");
            ImGui.Spacing();
            
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.2f, 0.2f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1.0f));
            
            if (ImGui.Button("Stop Live Logging", new Vector2(-1, 40)))
            {
                plugin.FFLogsService.StopLiveLog();
            }
            
            ImGui.PopStyleColor(3);
            
            if (plugin.FFLogsService.LiveFightCount > 0 && !string.IsNullOrEmpty(plugin.FFLogsService.CurrentReportCode))
            {
                ImGui.Spacing();
                if (ImGui.Button("View Parses", new Vector2(-1, 30)))
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
                GetDialogStartPath(),
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

        DrawStatus();
    }

    private void DrawSharedOptions()
    {
        ImGui.Text("Choose the guild you want to upload to:");
        
        var guildList = new List<(string? Id, string Name)> { (null, "Personal Logs") };
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

            // Show View Parses button when upload is complete
            if (statusMessage.StartsWith("Upload complete!") && plugin.FFLogsService.RecentReportCodes.Count > 0)
            {
                ImGui.Spacing();
                if (ImGui.Button("View Parses##status", new Vector2(-1, 30)))
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
            statusMessage = "Please enter a log directory path first.";
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
            await plugin.FFLogsService.StartLiveLogAsync(logPath, selectedRegion + 1, selectedVisibility, guildId, description, uploadPreviousFights);
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

        var effectivePath = LogFileHelper.GetLatestLogFileFromPath(logPath);
        if (effectivePath != logPath)
        {
            logPath = effectivePath;
        }

        if (!System.IO.File.Exists(logPath))
        {
            statusMessage = "No log files found in the specified directory.";
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
        var directoryToSave = logPath;
        if (System.IO.File.Exists(logPath))
        {
            directoryToSave = System.IO.Path.GetDirectoryName(logPath) ?? logPath;
        }
        
        plugin.Configuration.LogDirectory = directoryToSave;
        plugin.Configuration.Region = selectedRegion + 1;
        plugin.Configuration.Visibility = selectedVisibility;
        plugin.Configuration.Save();
    }
}
