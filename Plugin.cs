using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFLogsPlugin.Windows;
using FFLogsPlugin.Services;
using FFLogsPlugin.Helpers;

namespace FFLogsPlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/fflogsuploader";

    public Configuration Configuration { get; init; }
    public FFLogsService FFLogsService { get; init; }
    public ParserService ParserService { get; init; }
    public FFLogsApiService FFLogsApiService { get; init; }

    public readonly WindowSystem WindowSystem = new("FFLogsPlugin");
    private LoginWindow LoginWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private ParseViewerWindow ParseViewerWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        FFLogsService = new FFLogsService(this);
        ParserService = new ParserService(this);
        FFLogsApiService = new FFLogsApiService(this);

        // Initialize windows
        LoginWindow = new LoginWindow(this);
        MainWindow = new MainWindow(this);
        ParseViewerWindow = new ParseViewerWindow(this);

        WindowSystem.AddWindow(LoginWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ParseViewerWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 FFLogs 日志上传器"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("FFLogs 日志上传器已加载");
        
        // Attempt auto-login if credentials are saved
        _ = CheckAutoLoginAsync();
    }

    private async System.Threading.Tasks.Task CheckAutoLoginAsync()
    {
        if (Configuration.RememberCredentials && 
            !string.IsNullOrEmpty(Configuration.Email) && 
            !string.IsNullOrEmpty(Configuration.Password))
        {
             Log.Information("Auto-login: Credentials found, attempting login...");
             if (await FFLogsService.LoginAsync(Configuration.Email, Configuration.Password))
                 await StartConfiguredLiveLoggingAsync();
        }
    }

    public async System.Threading.Tasks.Task StartConfiguredLiveLoggingAsync()
    {
        if (!Configuration.AutoStartLiveLogging || FFLogsService.IsLiveLogging)
            return;

        var directory = Configuration.LogDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !LogFileHelper.IsValidLogPath(directory))
            directory = LogFileHelper.AutoDetectLogDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            Log.Warning("Auto live logging skipped: ACT log directory was not found.");
            return;
        }

        Configuration.LogDirectory = directory;
        Configuration.Save();
        Log.Information("Starting configured live logging.");
        await FFLogsService.StartLiveLogAsync(directory, Configuration.Region, Configuration.Visibility, Configuration.SelectedGuildId, string.Empty, true);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        LoginWindow.Dispose();
        MainWindow.Dispose();
        ParseViewerWindow.Dispose();

        // Stop live logging and wait for it to finish before disposing the parser engine,
        // otherwise the V8 engine could be disposed while still executing JS.
        FFLogsService.StopLiveLog();
        FFLogsService.WaitForLiveLogAsync().GetAwaiter().GetResult();
        ParserService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        
        if (FFLogsService.IsLoggedIn)
            MainWindow.Toggle();
        else
            LoginWindow.Toggle();
    }

    private void DrawUI() => WindowSystem.Draw();

    public void ToggleMainUi()
    {
        if (FFLogsService.IsLoggedIn)
            MainWindow.Toggle();
        else
            LoginWindow.Toggle();
    }

    public void ShowMainWindow()
    {
        LoginWindow.IsOpen = false;
        MainWindow.IsOpen = true;
    }

    public void ShowLoginWindow()
    {
        MainWindow.IsOpen = false;
        LoginWindow.IsOpen = true;
    }

    public void ShowParseViewer(string reportCode)
    {
        ParseViewerWindow.OpenWithReport(reportCode);
    }
}

