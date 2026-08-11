namespace WinLinScp.Services;

/// <summary>bash 单引号转义：' -> '\''，整串再包单引号。远端 shell 会剥掉最外层引号。</summary>
public static class ShellQuote
{
    public static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
