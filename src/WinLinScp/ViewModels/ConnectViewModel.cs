using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinLinScp.Models;
using WinLinScp.Services;

namespace WinLinScp.ViewModels;

public sealed record LogLine(string Text, bool IsError);

/// <summary>连接方式：登录脚本 / IP+密码直连。三选一改为二选一（别名登录已移除，脚本内部仍可经别名目标连接）。</summary>
public enum ConnectionMethod { Script, Password }

/// <summary>
/// 连接对话框：脚本=连接配置（执行 ps1 一次性模式验证 + 日志输出 + 自动提取 Target/WorkDir），
/// 或直接选 ~/.ssh/config 别名直连。Profile 持久化。
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly ProcessRunner _runner;
    private readonly SshService _ssh;
    private readonly IDialogService _dialogs;

    public ConnectViewModel(ProfileStore store, ProcessRunner runner, SshService ssh, IDialogService dialogs)
    {
        _store = store;
        _runner = runner;
        _ssh = ssh;
        _dialogs = dialogs;
        RefreshProfiles();
        if (_store.Settings.LastProfileName is { } last) LoadProfile(last);
    }

    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<LogLine> LogLines { get; } = new();

    [ObservableProperty]
    private string _profileName = "";

    [ObservableProperty]
    private string _scriptPath = "";

    [ObservableProperty]
    private string _hostAlias = "";

    [ObservableProperty]
    private string _host = "";

    [ObservableProperty]
    private string _user = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _workDir = "";

    /// <summary>当前连接方式（脚本 / IP+密码，界面据此显示对应字段）。</summary>
    [ObservableProperty]
    private ConnectionMethod _method = ConnectionMethod.Script;

    public bool IsScriptMethod => Method == ConnectionMethod.Script;
    public bool IsPasswordMethod => Method == ConnectionMethod.Password;

    partial void OnMethodChanged(ConnectionMethod value)
    {
        OnPropertyChanged(nameof(IsScriptMethod));
        OnPropertyChanged(nameof(IsPasswordMethod));
        // 切换方式时清掉其它方式的字段，避免保存/连接时"混合"误判
        if (value == ConnectionMethod.Script) { Host = ""; User = ""; Password = ""; }
        else { ScriptPath = ""; HostAlias = ""; }
    }

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusText = "";

    public string ResolvedAlias => Method == ConnectionMethod.Password
        ? (User.Trim().Length > 0 && Host.Trim().Length > 0 ? $"{User.Trim()}@{Host.Trim()}" : "")
        : HostAlias.Trim();
    public string ResolvedWorkDir => WorkDir.Trim().Length > 0 ? WorkDir.Trim() : "/";

    /// <summary>下次启动自动连接（持久化到 settings）。</summary>
    public bool AutoConnect
    {
        get => _store.Settings.AutoConnect;
        set
        {
            if (_store.Settings.AutoConnect == value) return;
            _store.Settings.AutoConnect = value;
            _store.Save();
            OnPropertyChanged();
        }
    }

    partial void OnScriptPathChanged(string value)
    {
        // 脚本变更时自动提取 Target/WorkDir（脚本是脚本方式的目标来源，别名输入框已移除）
        var (t, w) = LoginScriptParser.ParseFile(value);
        if (!string.IsNullOrEmpty(t)) HostAlias = t;
        if (!string.IsNullOrEmpty(w)) WorkDir = w;
    }

    public ConnectionProfile ToProfile()
    {
        var isPwd = Method == ConnectionMethod.Password;
        var isScript = Method == ConnectionMethod.Script;
        return new ConnectionProfile
        {
            Name = ProfileName.Trim(),
            ScriptPath = isScript && !string.IsNullOrWhiteSpace(ScriptPath) ? ScriptPath.Trim() : null,
            HostAlias = isPwd ? "" : HostAlias.Trim(),
            Host = isPwd && !string.IsNullOrWhiteSpace(Host) ? Host.Trim() : null,
            User = isPwd ? User.Trim() : "",
            Password = isPwd ? Password : "",
            WorkDir = ResolvedWorkDir,
        };
    }

    [RelayCommand]
    private void BrowseScript()
    {
        var path = _dialogs.OpenFile("选择登录脚本", "PowerShell 脚本 (*.ps1)|*.ps1|所有文件|*.*");
        if (!string.IsNullOrEmpty(path)) ScriptPath = path;
    }

    [RelayCommand]
    private void LoadProfile(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        var p = _store.Find(name);
        if (p is null) return;
        ProfileName = p.Name;
        // 纯别名配置（无脚本、无 Host）回退脚本方式——连接仍用 HostAlias，仅界面不再单独提供别名输入
        Method = !string.IsNullOrEmpty(p.ScriptPath) ? ConnectionMethod.Script
               : p.IsPasswordAuth ? ConnectionMethod.Password
               : ConnectionMethod.Script;
        ScriptPath = p.ScriptPath ?? "";
        if (Method == ConnectionMethod.Script)
        {
            // 脚本是权威目标来源：脚本解析的 Target/WorkDir 覆盖历史脏值（如别名下拉误存的显示串）
            var (t, w) = ScriptPath.Length > 0 && File.Exists(ScriptPath) ? LoginScriptParser.ParseFile(ScriptPath) : (null, null);
            HostAlias = !string.IsNullOrEmpty(t) ? t : p.HostAlias;
            WorkDir = !string.IsNullOrEmpty(w) ? w : p.WorkDir;
        }
        else
        {
            Host = p.Host ?? "";
            User = p.User;
            Password = p.Password;
            WorkDir = p.WorkDir;
        }
        StatusText = $"已载入配置：{p.Name}";
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (IsConnecting) return;

        // 按连接方式设认证上下文（连接测试阶段即生效）；其余方式走别名/脚本
        if (Method == ConnectionMethod.Password)
        {
            if (string.IsNullOrWhiteSpace(User) || string.IsNullOrWhiteSpace(Host))
            {
                _dialogs.Error("请填写 IP/主机名 与 用户名。");
                return;
            }
            SshAuthContext.SetPassword(Password);
        }
        else
        {
            if (string.IsNullOrEmpty(ResolvedAlias))
            {
                _dialogs.Error("请选择登录脚本（自动提取目标），或切换到 IP+密码 方式。");
                return;
            }
            SshAuthContext.Clear();
        }

        IsConnecting = true;
        IsConnected = false;
        LogLines.Clear();
        StatusText = "";
        try
        {
            if (Method == ConnectionMethod.Script && !string.IsNullOrEmpty(ScriptPath) && File.Exists(ScriptPath))
                await ConnectViaScriptAsync();
            else
                await ConnectViaAliasAsync();
        }
        catch (Exception ex)
        {
            Log("连接异常：" + ex.Message, true);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task ConnectViaScriptAsync()
    {
        Log($"执行登录脚本：{ScriptPath}", false);
        var args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath, "-RemoteCommand", "echo WinLinScp-OK" };
        var r = await _runner.RunStreamingAsync("powershell", args,
            line => Log("  " + line, false),
            line => Log("  " + line, true),
            CancellationToken.None, timeoutMs: 90_000);

        if (r.Ok)
        {
            // 脚本成功：用脚本解析的 Target/WorkDir 校正（覆盖历史脏值，保存后即干净）
            var (t, w) = LoginScriptParser.ParseFile(ScriptPath);
            if (!string.IsNullOrEmpty(t)) HostAlias = t;
            if (!string.IsNullOrEmpty(w)) WorkDir = w;
            IsConnected = true;
            Log($"登录成功：已连接 {ResolvedAlias}", false);
        }
        else
        {
            Log("登录失败：" + SshErrorTranslator.Describe(r, "登录"), true);
        }
    }

    private async Task ConnectViaAliasAsync()
    {
        Log($"直接连接：{ResolvedAlias}（工作目录 {ResolvedWorkDir}）", false);
        var cmd = $"mkdir -p {ShellQuote.Quote(ResolvedWorkDir)}; cd {ShellQuote.Quote(ResolvedWorkDir)} && echo WinLinScp-OK";
        var r = await _ssh.RunSimpleAsync(ResolvedAlias, cmd);
        if (r.Ok)
        {
            IsConnected = true;
            Log($"已连接 {ResolvedAlias} · {ResolvedWorkDir}", false);
        }
        else
        {
            Log("连接失败：" + SshErrorTranslator.Describe(r, "连接"), true);
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = ProfileName.Trim();
        if (name.Length == 0) { _dialogs.Error("请输入配置名称。"); return; }

        if (Method == ConnectionMethod.Password && (string.IsNullOrWhiteSpace(User) || string.IsNullOrWhiteSpace(Host)))
        { _dialogs.Error("请填写 IP/主机 与 用户名。"); return; }
        if (Method == ConnectionMethod.Script && string.IsNullOrWhiteSpace(ScriptPath))
        { _dialogs.Error("请选择登录脚本。"); return; }

        var p = ToProfile();
        p.Name = name;
        _store.Upsert(p);
        _store.Settings.LastProfileName = name;
        _store.Save();
        RefreshProfiles();
        StatusText = "已保存配置：" + name;
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var name = ProfileName.Trim();
        if (name.Length == 0) return;
        if (!_dialogs.Confirm($"删除配置「{name}」？", "删除确认")) return;
        _store.Delete(name);
        _store.Save();
        RefreshProfiles();
        StatusText = "已删除配置";
    }

    private void RefreshProfiles()
    {
        ProfileNames.Clear();
        foreach (var p in _store.Profiles) ProfileNames.Add(p.Name);
    }

    private void Log(string text, bool isError)
    {
        LogLines.Add(new LogLine(text, isError));
        StatusText = text;
    }
}
