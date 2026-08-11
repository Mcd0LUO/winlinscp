using System.Windows;
using WinLinScp.Services;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

public partial class UploadConfirmDialog : Window
{
    public UploadPlan? Plan { get; private set; }

    public UploadConfirmDialog(UploadPreview preview)
    {
        InitializeComponent();
        TxtSummary.Text = $"将上传 {preview.Count} 项（共 {SizeFormatter.Format(preview.TotalBytes)}）到：";
        TxtDestination.Text = preview.Destination;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Plan = new UploadPlan
        {
            Mode = RadioZip.IsChecked == true ? PackMode.Zip
                 : RadioNone.IsChecked == true ? PackMode.None
                 : PackMode.Tar,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
