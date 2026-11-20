using System.IO;

namespace AiPassportVoice.App.Services;

/// <summary>
/// 运行日志落盘（按天切分，exe 同级 logs 目录）。
/// 由 voice-config.json 的 log_to_file 开关控制，写失败静默忽略。
/// </summary>
public static class FileLogger
{
    private static readonly object Gate = new();
    private static string? _logDir;

    public static bool Enabled { get; set; }

    public static string LogDirectory => _logDir ??= Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Write(string line)
    {
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
