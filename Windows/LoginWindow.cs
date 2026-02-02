using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FFLogsPlugin.Windows;

public class LoginWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    
    private string email = string.Empty;
    private string password = string.Empty;
    private bool rememberMe = false;
    private string statusMessage = string.Empty;
    private bool isLoggingIn = false;

    public LoginWindow(Plugin plugin)
        : base("FFLogs Login##FFLogsLogin", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        
        Size = new Vector2(350, 200);
        SizeCondition = ImGuiCond.Always;

        // Load saved credentials if available
        if (plugin.Configuration.RememberCredentials)
        {
            email = plugin.Configuration.Email;
            password = plugin.Configuration.Password;
            rememberMe = true;
        }
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text("Log in to FFLogs");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##email", "Email", ref email, 256);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##password", "Password", ref password, 256, ImGuiInputTextFlags.Password);

        ImGui.Checkbox("Remember me", ref rememberMe);

        ImGui.Spacing();

        if (isLoggingIn)
            ImGui.BeginDisabled();

        if (ImGui.Button(isLoggingIn ? "Logging in..." : "Login", new Vector2(-1, 30)))
        {
            _ = DoLogin();
        }

        if (isLoggingIn)
            ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Spacing();
            var color = statusMessage.StartsWith("Error") 
                ? new Vector4(1, 0.3f, 0.3f, 1) 
                : new Vector4(0.3f, 1, 0.3f, 1);
            ImGui.TextColored(color, statusMessage);
        }
    }

    private async System.Threading.Tasks.Task DoLogin()
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            statusMessage = "Error: Please enter email and password";
            return;
        }

        isLoggingIn = true;
        statusMessage = "Logging in...";

        try
        {
            var success = await plugin.FFLogsService.LoginAsync(email, password);

            if (success)
            {
                if (rememberMe)
                {
                    plugin.Configuration.Email = email;
                    plugin.Configuration.Password = password;
                    plugin.Configuration.RememberCredentials = true;
                }
                else
                {
                    plugin.Configuration.Email = string.Empty;
                    plugin.Configuration.Password = string.Empty;
                    plugin.Configuration.RememberCredentials = false;
                }
                plugin.Configuration.Save();

                statusMessage = "Login successful!";
                plugin.ShowMainWindow();
            }
            else
            {
                statusMessage = "Error: Login failed. Check credentials.";
            }
        }
        catch (Exception ex)
        {
            statusMessage = $"Error: {ex.Message}";
            Plugin.Log.Error(ex, "Login failed");
        }
        finally
        {
            isLoggingIn = false;
        }
    }
}
