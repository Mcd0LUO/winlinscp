using System.IO.Compression;

namespace WinLinScp.ViewModels;

/// <summary>View 层实现的对话框服务（弹窗/文本输入），注入给 ViewModel 使用。</summary>
public interface IDialogService
{
    /// <summary>文本输入对话框；返回输入值，取消返回 null。</summary>
    string? PromptText(string title, string prompt, string initial = "");

    bool Confirm(string message, string title = "确认");

    void Info(string message, string title = "WinLinScp");

    void Error(string message, string title = "WinLinScp");

    /// <summary>保存文件对话框；返回选择路径，取消返回 null。</summary>
    string? SaveFile(string title, string defaultName);

    /// <summary>打开文件对话框；返回选择路径，取消返回 null。</summary>
    string? OpenFile(string title, string filter);

    /// <summary>多选上传确认弹窗（含打包选项）；返回打包方案，取消返回 null。</summary>
    UploadPlan? ConfirmUpload(UploadPreview preview);

    /// <summary>本地 zip 压缩等级选择；返回等级，取消返回 null。</summary>
    CompressionLevel? ChooseCompressionLevel();

    /// <summary>模态显示一段文本输出（自定义脚本执行结果等）。</summary>
    void ShowOutput(string title, string text);
}
