using System.Windows;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

public partial class RemoteViewerWindow : Window
{
    public RemoteViewerWindow(ViewerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Title = "查看 - " + vm.DisplayName;
    }
}
