using System.IO.Compression;
using System.Windows;

namespace WinLinScp.Views;

public partial class CompressionLevelDialog : Window
{
    public CompressionLevel? Level { get; private set; }

    public CompressionLevelDialog(CompressionLevel initial = CompressionLevel.Optimal)
    {
        InitializeComponent();
        RdoOptimal.IsChecked = initial == CompressionLevel.Optimal;
        RdoFastest.IsChecked = initial == CompressionLevel.Fastest;
        RdoSmallest.IsChecked = initial == CompressionLevel.SmallestSize;
        RdoNone.IsChecked = initial == CompressionLevel.NoCompression;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Level = RdoNone.IsChecked == true ? CompressionLevel.NoCompression
              : RdoFastest.IsChecked == true ? CompressionLevel.Fastest
              : RdoSmallest.IsChecked == true ? CompressionLevel.SmallestSize
              : CompressionLevel.Optimal;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
