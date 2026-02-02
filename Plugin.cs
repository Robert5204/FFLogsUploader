using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFLogsPlugin.Windows;
using FFLogsPlugin.Services;

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

    public readonly WindowSystem WindowSystem = new("FFLogsPlugin");
    private LoginWindow LoginWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        FFLogsService = new FFLogsService(this);
        ParserService = new ParserService(this);

        // Initialize windows
        LoginWindow = new LoginWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(LoginWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the FFLogs uploader window"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("FFLogs Plugin loaded!");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        LoginWindow.Dispose();
        MainWindow.Dispose();
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
}
