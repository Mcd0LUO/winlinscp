using System.Globalization;
using System.Text;

namespace WinLinScp.Services;

/// <summary>字节 → 编码启发式检测（BOM / UTF-8 严格 / 系统 ANSI 代码页）。</summary>
public static class EncodingDetector
{
    public static Encoding Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        if (IsValidUtf8(bytes))
            return new UTF8Encoding(false);
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
        catch { return Encoding.Latin1; }
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
            enc.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
