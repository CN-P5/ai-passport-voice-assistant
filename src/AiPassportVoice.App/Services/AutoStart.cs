using Microsoft.Win32;

namespace AiPassportVoice.App.Services;

/// <summary>开机自启（当前用户 Run 项，无需管理员权限）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "AiPassportVoice";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(RunName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }
}
