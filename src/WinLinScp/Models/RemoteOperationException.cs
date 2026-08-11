namespace WinLinScp.Models;

/// <summary>远端操作失败（含 ssh/scp 错误），Message 已是中文。</summary>
public sealed class RemoteOperationException : Exception
{
    public RemoteOperationException(string message) : base(message) { }
}
