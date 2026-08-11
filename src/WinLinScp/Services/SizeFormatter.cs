namespace WinLinScp.Services;

/// <summary>字节数/速度 → 人类可读文本（B/KB/MB/GB/TB），全应用唯一实现。</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) return "";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {Units[i]}";
    }

    /// <summary>速度，如 "12.3 MB/s"。</summary>
    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 0) return "";
        double v = bytesPerSec;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytesPerSec:0} B/s" : $"{v:0.#} {Units[i]}/s";
    }

    /// <summary>预计剩余时间，如 "剩余 12 秒" / "剩余 2 分 5 秒" / "剩余 1 小时 3 分"。</summary>
    public static string FormatEta(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return "剩余 1 秒";
        if (ts.TotalSeconds < 60) return $"剩余 {Math.Max(1, (int)ts.TotalSeconds)} 秒";
        if (ts.TotalMinutes < 60)
        {
            var m = (int)ts.TotalMinutes;
            var s = ts.Seconds;
            return s > 0 ? $"剩余 {m} 分 {s} 秒" : $"剩余 {m} 分";
        }
        return $"剩余 {(int)ts.TotalHours} 小时 {ts.Minutes} 分";
    }
}
