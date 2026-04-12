using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OsmoActionViewer.Utils;
using OsmoActionViewer.ViewModels;

namespace OsmoActionViewer.Views;

public partial class RecordingDetailView : UserControl
{
    private ViewerViewModel? _vm;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _metadataSaveTimer;
    private bool _isPlaying;
    private bool _isUpdatingSeekBar;
    private bool _isDraggingSeekBar;
    private bool _isSeekBarPressed;
    private bool _isSyncingMetadataFields;

    public RecordingDetailView()
    {
        InitializeComponent();
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();
        _metadataSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _metadataSaveTimer.Tick += MetadataSaveTimer_Tick;
        Loaded += OnLoaded;
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekBar_DragStarted));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekBar_DragCompleted));
        UpdateMapsButtonState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewerViewModel vm) return;
        if (_vm == vm) return;
        _vm = vm;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.CurrentMarkers.CollectionChanged += (_, _) => RefreshMarkers();
        SyncFields();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.CurrentMediaUri):
                if (_vm.CurrentMediaUri != null)
                {
                    ResetSeekBar();
                    Media.Source = _vm.CurrentMediaUri;
                    Media.Play();
                    _isPlaying = true;
                }
                else
                {
                    Media.Stop();
                    Media.Source = null;
                    _isPlaying = false;
                    ResetSeekBar();
                }
                break;
            case nameof(ViewerViewModel.EditingTitle):
                if (TitleBox.Text != _vm.EditingTitle)
                {
                    _isSyncingMetadataFields = true;
                    TitleBox.Text = _vm.EditingTitle;
                    _isSyncingMetadataFields = false;
                }
                break;
            case nameof(ViewerViewModel.EditingLocationText):
                if (LocationBox.Text != _vm.EditingLocationText)
                {
                    _isSyncingMetadataFields = true;
                    LocationBox.Text = _vm.EditingLocationText;
                    _isSyncingMetadataFields = false;
                }
                break;
            case nameof(ViewerViewModel.EditingGoogleMapsUrl):
                if (GMapsBox.Text != _vm.EditingGoogleMapsUrl)
                {
                    _isSyncingMetadataFields = true;
                    GMapsBox.Text = _vm.EditingGoogleMapsUrl;
                    _isSyncingMetadataFields = false;
                    UpdateMapsButtonState();
                }
                break;
            case nameof(ViewerViewModel.ErrorMessage):
                ErrorText.Text = _vm.ErrorMessage ?? "";
                break;
        }
    }

    private void SyncFields()
    {
        if (_vm == null) return;
        _isSyncingMetadataFields = true;
        TitleBox.Text = _vm.EditingTitle;
        LocationBox.Text = _vm.EditingLocationText;
        GMapsBox.Text = _vm.EditingGoogleMapsUrl;
        _isSyncingMetadataFields = false;
        UpdateMapsButtonState();
        RefreshMarkers();
    }

    private void RefreshMarkers()
    {
        MarkerList.Items.Clear();
        if (_vm == null) return;
        foreach (var m in _vm.CurrentMarkers)
        {
            MarkerList.Items.Add($"{m:F1} s  ({TimeFormat.FormatSeconds(m)})");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        if (Media.NaturalDuration.HasTimeSpan)
        {
            var current = Media.Position.TotalSeconds;
            var total = Media.NaturalDuration.TimeSpan.TotalSeconds;
            _vm.CurrentPlaybackSeconds = current;
            UpdateTimeAndSeekBar(current, total);
        }
    }

    public void SeekRelative(double deltaSeconds)
    {
        if (Media.Source == null) return;
        var pos = Media.Position.TotalSeconds + deltaSeconds;
        if (pos < 0) pos = 0;
        if (Media.NaturalDuration.HasTimeSpan)
        {
            pos = Math.Min(pos, Media.NaturalDuration.TimeSpan.TotalSeconds);
        }
        Media.Position = TimeSpan.FromSeconds(pos);
        if (_vm != null) _vm.CurrentPlaybackSeconds = pos;
        if (Media.NaturalDuration.HasTimeSpan)
        {
            UpdateTimeAndSeekBar(pos, Media.NaturalDuration.TimeSpan.TotalSeconds);
        }
    }

    public void TogglePlayPause()
    {
        if (Media.Source == null) return;
        if (_isPlaying) { Media.Pause(); _isPlaying = false; }
        else { Media.Play(); _isPlaying = true; }
    }

    private void Back10Button_Click(object sender, RoutedEventArgs e) => SeekRelative(-10);
    private void Forward10Button_Click(object sender, RoutedEventArgs e) => SeekRelative(10);
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!Media.NaturalDuration.HasTimeSpan) return;
        UpdateTimeAndSeekBar(0, Media.NaturalDuration.TimeSpan.TotalSeconds);
    }

    private async void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        if (_vm == null) return;
        await _vm.RecoverPlaybackForSelectedRecordingAsync(e.ErrorException?.Message);
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        if (Media.NaturalDuration.HasTimeSpan)
        {
            var total = Media.NaturalDuration.TimeSpan.TotalSeconds;
            if (_vm != null) _vm.CurrentPlaybackSeconds = total;
            UpdateTimeAndSeekBar(total, total);
        }
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingSeekBar || Media.Source == null || !Media.NaturalDuration.HasTimeSpan) return;
        if (_isDraggingSeekBar || _isSeekBarPressed)
        {
            TimeText.Text = $"{TimeFormat.FormatSeconds(SeekBar.Value)} / {TimeFormat.FormatSeconds(Media.NaturalDuration.TimeSpan.TotalSeconds)}";
            return;
        }
        if (Math.Abs(Media.Position.TotalSeconds - SeekBar.Value) < 0.25) return;

        CommitSeekBarPosition();
    }

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingSeekBar = true;
    }

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingSeekBar = false;
        CommitSeekBarPosition();
    }

    private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeekBarPressed = true;
    }

    private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isSeekBarPressed = false;
        CommitSeekBarPosition();
    }

    private void SeekBar_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isSeekBarPressed) return;
        _isSeekBarPressed = false;
        CommitSeekBarPosition();
    }

    private void UpdateTimeAndSeekBar(double currentSeconds, double totalSeconds)
    {
        _isUpdatingSeekBar = true;
        SeekBar.Maximum = Math.Max(1, totalSeconds);
        if (!_isDraggingSeekBar)
        {
            SeekBar.Value = Math.Max(0, Math.Min(currentSeconds, totalSeconds));
        }
        _isUpdatingSeekBar = false;
        TimeText.Text = $"{TimeFormat.FormatSeconds(currentSeconds)} / {TimeFormat.FormatSeconds(totalSeconds)}";
    }

    private void ResetSeekBar()
    {
        _isUpdatingSeekBar = true;
        SeekBar.Minimum = 0;
        SeekBar.Maximum = 1;
        SeekBar.Value = 0;
        _isUpdatingSeekBar = false;
        TimeText.Text = "00:00 / 00:00";
    }

    private void CommitSeekBarPosition()
    {
        if (Media.Source == null || !Media.NaturalDuration.HasTimeSpan) return;
        var total = Media.NaturalDuration.TimeSpan.TotalSeconds;
        var target = Math.Max(0, Math.Min(SeekBar.Value, total));
        Media.Position = TimeSpan.FromSeconds(target);
        if (_vm != null) _vm.CurrentPlaybackSeconds = target;
        UpdateTimeAndSeekBar(target, total);
    }

    private void MetadataField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingMetadataFields) return;
        UpdateMapsButtonState();
        ScheduleMetadataAutoSave();
    }

    private void MetadataSaveTimer_Tick(object? sender, EventArgs e)
    {
        _metadataSaveTimer.Stop();
        SaveMetadataFromFields();
    }

    private void ScheduleMetadataAutoSave()
    {
        if (_vm?.SelectedRecording == null) return;
        _metadataSaveTimer.Stop();
        _metadataSaveTimer.Start();
    }

    private void SaveMetadataFromFields()
    {
        if (_vm == null || _vm.SelectedRecording == null) return;
        _vm.EditingTitle = TitleBox.Text;
        _vm.EditingLocationText = LocationBox.Text;
        _vm.EditingGoogleMapsUrl = GMapsBox.Text;
        _vm.PersistEditingMetadata();
        UpdateMapsButtonState();
    }

    private void UpdateMapsButtonState()
    {
        OpenGMapsButton.IsEnabled = _vm?.ValidatedGoogleMapsUrl() != null ||
                                    (!string.IsNullOrWhiteSpace(GMapsBox.Text) &&
                                     Uri.TryCreate(GMapsBox.Text, UriKind.Absolute, out _));
    }

    private void SaveMetaButton_Click(object sender, RoutedEventArgs e)
    {
        SaveMetadataFromFields();
    }

    private void OpenGMapsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        SaveMetadataFromFields();
        var url = _vm.ValidatedGoogleMapsUrl();
        if (url == null) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private void AddMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.MarkerInputSeconds = MarkerSecondsBox.Text;
        _vm.AddMarkerFromInput();
        MarkerSecondsBox.Text = "";
    }

    private void AddNowMarkerButton_Click(object sender, RoutedEventArgs e) => _vm?.AddMarkerAtCurrentTime();

    private void RemoveMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (MarkerList.SelectedIndex < 0) return;
        if (MarkerList.SelectedIndex < _vm.CurrentMarkers.Count)
        {
            _vm.RemoveMarker(_vm.CurrentMarkers[MarkerList.SelectedIndex]);
        }
    }

    private async void ExportHighlightsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.SelectedRecording == null) return;
        _vm.MarkerClipDurationSecondsText = ClipDurationBox.Text;
        var dlg = new SaveFileDialog
        {
            Title = "Export Marker Highlights",
            FileName = _vm.DefaultHighlightsFileName(_vm.SelectedRecording),
            Filter = "MP4 (*.mp4)|*.mp4",
        };
        if (dlg.ShowDialog() != true) return;
        await _vm.ExportHighlightsFromMarkersAsync(dlg.FileName);
    }

    private void StartNowButton_Click(object sender, RoutedEventArgs e)
    {
        _vm?.SetExportStartFromCurrentTime();
        if (_vm != null) StartBox.Text = _vm.ExportStartSecondsText;
    }

    private void EndNowButton_Click(object sender, RoutedEventArgs e)
    {
        _vm?.SetExportEndFromCurrentTime();
        if (_vm != null) EndBox.Text = _vm.ExportEndSecondsText;
    }

    private async void ExportRangeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.SelectedRecording == null) return;
        _vm.ExportStartSecondsText = StartBox.Text;
        _vm.ExportEndSecondsText = EndBox.Text;
        if (!double.TryParse(StartBox.Text, out var s) || !double.TryParse(EndBox.Text, out var en)) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export Clipped Video",
            FileName = _vm.DefaultRangeFileName(_vm.SelectedRecording, s, en),
            Filter = "MP4 (*.mp4)|*.mp4",
        };
        if (dlg.ShowDialog() != true) return;
        await _vm.ExportSelectedRangeAsync(dlg.FileName);
    }
}
