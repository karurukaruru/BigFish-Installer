using System.ComponentModel;
using System.Windows;

namespace BigFish.App;
public partial class MainWindow : Window
{
    private bool closing;
    public MainWindow() { InitializeComponent(); Closing += OnClosing; }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing || DataContext is not MainViewModel vm) return;
        e.Cancel = true; IsEnabled = false;
        try { await vm.ShutdownAsync(); }
        catch (Exception ex) { MessageBox.Show(BigFish.Core.LogManager.Redact(ex.Message), "停止服务时发生错误", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { closing = true; Close(); }
    }
}
