using System.Windows;

namespace WinLinScp.Views;

public partial class OutputWindow : Window
{
    public OutputWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        OutputBox.Text = text;
        Loaded += (_, _) => OutputBox.ScrollToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
