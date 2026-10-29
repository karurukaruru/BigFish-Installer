using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;

namespace BigFish.App;
public partial class MainWindow : Window
{
    private bool closing;
    public MainWindow() { InitializeComponent(); Closing += OnClosing; }
    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.KeyPresent = ApiKeyInput.Password.Length > 0;
    }
    private async void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanSaveKey) return;
        try { await vm.SaveApiKeyAsync(ApiKeyInput.Password); }
        finally { ApiKeyInput.Clear(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing || DataContext is not MainViewModel vm) return;
        e.Cancel = true; IsEnabled = false;
        try { await vm.ShutdownAsync(); }
        catch (Exception ex) { MessageBox.Show(BigFish.Core.LogManager.Redact(ex.Message), "停止服务时发生错误", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { closing = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
