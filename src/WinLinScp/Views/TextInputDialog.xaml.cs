using System.Windows;
using System.Windows.Input;

namespace WinLinScp.Views;

public partial class TextInputDialog : Window
{
    public string Value => TxtValue.Text;

    public TextInputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        TxtPrompt.Text = prompt;
        TxtValue.Text = initial;
        Loaded += (_, _) =>
        {
            TxtValue.SelectAll();
            TxtValue.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TxtValue_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
