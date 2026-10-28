using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace VideoView.App;

public partial class MainWindow
{
    private void UsageNotice_Click(object sender, RoutedEventArgs e) => ShowUsageNotices();

    private void ShowUsageNotices()
    {
        ShowPage("settings");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            PageScroll.UpdateLayout();
            UsageNoticeHeading.Focus();
            PageScroll.ScrollToVerticalOffset(UsageNoticeCard.TranslatePoint(new Point(), (UIElement)PageScroll.Content).Y);
        }));
    }

    private void License_Expanded(object sender, RoutedEventArgs e)
    {
        if (LicenseText.Text.Length != 0) return;
        // Embed the unmodified license so it is also available offline and in development builds.
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("VideoView.LICENSE")
            ?? throw new InvalidOperationException("GPL 라이선스 리소스를 찾을 수 없습니다.");
        using var reader = new StreamReader(stream);
        LicenseText.Text = reader.ReadToEnd();
    }

    private void ProjectLink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            UsageLinkStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UsageLinkStatus.Text = $"브라우저를 열지 못했습니다. 주소: {e.Uri.AbsoluteUri}";
            UsageLinkStatus.Visibility = Visibility.Visible;
        }
    }
}
