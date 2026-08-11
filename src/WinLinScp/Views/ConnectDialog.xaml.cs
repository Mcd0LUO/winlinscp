using System.Windows;
using WinLinScp.Models;
using WinLinScp.ViewModels;

namespace WinLinScp.Views;

public partial class ConnectDialog : Window
{
    public ConnectDialog(ConnectViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        ProfileCombo.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is string name)
                vm.LoadProfileCommand.Execute(name);
        };

        // PasswordBox 不能直接绑定 Password：双向同步（载入配置回填 + 输入回写）
        PasswordBox.PasswordChanged += (_, _) => vm.Password = PasswordBox.Password;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectViewModel.Password) && PasswordBox.Password != vm.Password)
                PasswordBox.Password = vm.Password;
        };
    }

    public ConnectionProfile? Profile => (DataContext as ConnectViewModel)?.ToProfile();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectViewModel { IsConnected: true })
            DialogResult = true;
    }
}
