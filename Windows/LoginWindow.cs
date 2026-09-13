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
        : base("FFLogs 登录##FFLogsLogin", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        
        Size = new Vector2(350, 200);
        SizeCondition = ImGuiCond.Always;

        // Load saved credentials if available
        if (plugin.Configuration.RememberCredentials)
        {
            email = plugin.Configuration.Email;
            password = plugin.Configuration.Password; // Transparently decrypted via DPAPI
            rememberMe = true;
        }
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text("登录 FFLogs");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##email", "邮箱", ref email, 256);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##password", "密码", ref password, 256, ImGuiInputTextFlags.Password);

        ImGui.Checkbox("记住登录信息", ref rememberMe);

        ImGui.Spacing();

        if (isLoggingIn)
            ImGui.BeginDisabled();

        if (ImGui.Button(isLoggingIn ? "登录中..." : "登录", new Vector2(-1, 30)))
        {
            _ = DoLogin();
        }

        if (isLoggingIn)
            ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.Spacing();
            var color = statusMessage.StartsWith("错误") 
                ? new Vector4(1, 0.3f, 0.3f, 1) 
                : new Vector4(0.3f, 1, 0.3f, 1);
            ImGui.TextColored(color, statusMessage);
        }
    }

    private async System.Threading.Tasks.Task DoLogin()
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            statusMessage = "错误：请输入邮箱和密码";
            return;
        }

        isLoggingIn = true;
        statusMessage = "登录中...";

        try
        {
            var success = await plugin.FFLogsService.LoginAsync(email, password);

            if (success)
            {
                if (rememberMe)
                {
                    plugin.Configuration.Email = email;
                    plugin.Configuration.Password = password; // Transparently encrypted via DPAPI
                    plugin.Configuration.RememberCredentials = true;
                }
                else
                {
                    plugin.Configuration.Email = string.Empty;
                    plugin.Configuration.Password = string.Empty;
                    plugin.Configuration.RememberCredentials = false;
                }
                plugin.Configuration.Save();

                statusMessage = "登录成功！";
                plugin.ShowMainWindow();
                await plugin.StartConfiguredLiveLoggingAsync();
            }
            else
            {
                statusMessage = "错误：登录失败，请检查账号密码。";
            }
        }
        catch (Exception ex)
        {
            statusMessage = $"错误：{ex.Message}";
            Plugin.Log.Error(ex, "Login failed");
        }
        finally
        {
            isLoggingIn = false;
        }
    }
}

