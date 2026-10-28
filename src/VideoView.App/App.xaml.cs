using System.Windows;

namespace VideoView.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var instanceName = "Local\\VideoView.Desktop";
        var dataIndex = Array.IndexOf(e.Args, "--data-directory");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length)
        {
            var dataPath = System.IO.Path.GetFullPath(e.Args[dataIndex + 1]).ToUpperInvariant();
            instanceName += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataPath)))[..16];
        }
        _instance = new Mutex(true, instanceName, out var first);
        if (!first && !e.Args.Contains("--ui-smoke"))
        {
            MessageBox.Show("VideoView가 이미 실행 중입니다. 작업 표시줄의 트레이 아이콘을 열어 주세요.", "VideoView");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "VideoView · 작업 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
