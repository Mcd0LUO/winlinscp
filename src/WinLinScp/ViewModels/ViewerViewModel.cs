using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinLinScp.Services;

namespace WinLinScp.ViewModels;

/// <summary>远端文件查看器（预览 + 编码切换 + 用默认程序打开 + 保存）。</summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    private const int MaxPreviewBytes = 4 * 1024 * 1024;
    private readonly SshService _ssh;
    private readonly ScpService _scp;
    private readonly IDialogService _dialogs;
    private readonly string _alias;
    private byte[] _bytes = Array.Empty<byte>();
    private bool _truncated;

    public string RemotePath { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _encodingName = "自动";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isLoading;

    public IReadOnlyList<string> Encodings { get; } = ["自动", "UTF-8", "GBK", "UTF-16 LE", "Latin-1"];

    public ViewerViewModel(SshService ssh, ScpService scp, IDialogService dialogs,
        string alias, string remotePath, string name)
    {
        _ssh = ssh;
        _scp = scp;
        _dialogs = dialogs;
        _alias = alias;
        RemotePath = remotePath;
        DisplayName = name;
        _ = LoadAsync();
    }

    partial void OnEncodingNameChanged(string value) => Decode();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var (bytes, truncated) = await _ssh.RunCommandByteLimitAsync(
                _alias, RemoteOps.CatCommand(RemotePath), MaxPreviewBytes, CancellationToken.None);
            _bytes = bytes;
            _truncated = truncated;
            Decode();
            StatusText = (truncated ? $"文件较大，仅预览前 {SizeFormatter.Format(bytes.Length)}。 " : "")
                         + $"大小 {SizeFormatter.Format(bytes.Length)}";
        }
        catch (Exception ex)
        {
            StatusText = "读取失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Decode()
    {
        if (_bytes.Length == 0) { Text = ""; return; }
        var enc = EncodingName switch
        {
            "UTF-8" => new UTF8Encoding(false),
            "GBK" => Encoding.GetEncoding(936),
            "UTF-16 LE" => Encoding.Unicode,
            "Latin-1" => Encoding.Latin1,
            _ => EncodingDetector.Detect(_bytes),
        };
        Text = enc.GetString(_bytes);
    }

    [RelayCommand]
    private async Task Reload() => await LoadAsync();

    [RelayCommand]
    private async Task OpenWithDefaultApp()
    {
        var tmp = TempFileTracker.GetTempPath(DisplayName);
        var r = await _scp.DownloadToFileAsync(_alias, RemotePath, tmp, CancellationToken.None);
        if (!r.Ok) { _dialogs.Error(SshErrorTranslator.Describe(r, "下载")); return; }
        try { Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true }); }
        catch (Exception ex) { _dialogs.Error(ex.Message, "打开失败"); }
    }

    [RelayCommand]
    private async Task SaveToLocal()
    {
        var path = _dialogs.SaveFile("保存远端文件", DisplayName);
        if (string.IsNullOrEmpty(path)) return;
        var r = await _scp.DownloadToFileAsync(_alias, RemotePath, path, CancellationToken.None);
        if (r.Ok) _dialogs.Info("已保存：" + path);
        else _dialogs.Error(SshErrorTranslator.Describe(r, "下载"));
    }
}
