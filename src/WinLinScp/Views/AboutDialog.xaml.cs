using System.Reflection;
using System.Windows;

namespace WinLinScp.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VerText.Text = ver is null ? "" : $"版本 {ver.ToString(3)}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
