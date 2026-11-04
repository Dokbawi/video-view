using System.Windows;
using System.Windows.Threading;

namespace VideoView.App;

public partial class MainWindow
{
    private DispatcherTimer? _previewVolumeSave;

    private void PreviewVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || Smoke) return;
        _settings.PreviewVolume = e.NewValue;
        if (_previewVolumeSave == null)
        {
            _previewVolumeSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _previewVolumeSave.Tick += (_, _) =>
            {
                _previewVolumeSave.Stop();
                try { _store.Save(_settings); }
                catch (Exception error) { SetStatus("미리듣기 음량을 저장하지 못했습니다. " + error.Message, true); }
            };
            Closed += (_, _) => _previewVolumeSave.Stop();
        }
        _previewVolumeSave.Stop(); _previewVolumeSave.Start();
    }
}
