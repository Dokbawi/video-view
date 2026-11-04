using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VideoView.Platform.Audio;
using UserControl = System.Windows.Controls.UserControl;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace VideoView.App.Controls;

public partial class ListeningVolumeControl : UserControl
{
    private readonly ListeningVolumeService _service = new();
    private bool _working;
    public ListeningVolumeControl()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh_Click(this, new RoutedEventArgs()); };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var selected = (Devices.SelectedItem as OutputVolumeSnapshot)?.Id;
        SetWorking(true);
        try
        {
            var devices = await Task.Run(_service.ReadOutputs);
            Devices.ItemsSource = devices;
            Devices.SelectedItem = devices.FirstOrDefault(device => device.Id == selected) ?? devices.FirstOrDefault();
            Status.Text = devices.Count == 0 ? "연결된 출력 장치가 없습니다." : "사용 중인 이어폰·헤드폰을 선택하세요. 아직 음량은 바뀌지 않았습니다.";
        }
        catch (Exception error) { Devices.ItemsSource = null; Status.Text = "장치를 읽지 못했습니다. " + error.Message; }
        finally { SetWorking(false); ShowLevel(); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_working || Devices.SelectedItem is not OutputVolumeSnapshot snapshot) return;
        var reduction = double.Parse((string)((ComboBoxItem)Reduction.SelectedItem).Tag, CultureInfo.InvariantCulture);
        SetWorking(true);
        try
        {
            var result = await Task.Run(() => _service.Reduce(snapshot, reduction));
            var devices = ((IEnumerable<OutputVolumeSnapshot>)Devices.ItemsSource).Select(device => device.Id == result.Id ? result : device).ToArray();
            Devices.ItemsSource = devices; Devices.SelectedItem = result;
            Status.Text = $"{snapshot.Decibels:0.0} → {result.Decibels:0.0} dB로 낮췄습니다. 앱별 볼륨은 조금씩 조절하세요.";
        }
        catch (Exception error)
        {
            // Require a fresh read after hotplug or external volume changes. Never restore a stale, louder level.
            Devices.ItemsSource = null;
            Status.Text = "적용을 확인하지 못했습니다. 새로고침하세요. " + error.Message;
        }
        finally { SetWorking(false); ShowLevel(); }
    }

    private void SetWorking(bool working)
    {
        _working = working; Devices.IsEnabled = Reduction.IsEnabled = !working;
        if (ApplyButton != null) ApplyButton.IsEnabled = !working && Devices.SelectedItem is OutputVolumeSnapshot;
    }
    private void Device_Changed(object sender, SelectionChangedEventArgs e) => ShowLevel();
    private void ShowLevel()
    {
        if (CurrentLevel == null || ApplyButton == null) return;
        var device = Devices.SelectedItem as OutputVolumeSnapshot;
        ApplyButton.IsEnabled = !_working && device != null;
        CurrentLevel.Text = device == null ? "새로고침으로 출력 장치를 확인하세요."
            : $"Windows {device.Scalar * 100:0.#}% · {device.Decibels:0.0} dB{(device.Muted ? " · 음소거 중" : "")}\n장치 지원 범위 {device.MinimumDecibels:0.#}~{device.MaximumDecibels:0.#} dB";
    }
}
