using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OsmoActionViewer.Models;
using OsmoActionViewer.ViewModels;

namespace OsmoActionViewer.Views;

public partial class RecordingListView : UserControl
{
    public ObservableCollection<RecordingListItem> Items { get; } = new();

    private ViewerViewModel? _vm;

    public RecordingListView()
    {
        InitializeComponent();
        RecordingListBox.ItemsSource = Items;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewerViewModel vm) return;
        if (_vm == vm) return;
        _vm = vm;
        vm.PropertyChanged += Vm_PropertyChanged;
        vm.Sections.CollectionChanged += (_, _) => RefreshSections();
        vm.Recordings.CollectionChanged += (_, _) => RefreshItems();
        RefreshSections();
        RefreshItems();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(ViewerViewModel.FolderPath))
        {
            FolderPathText.Text = _vm.FolderPath ?? "";
        }
        else if (e.PropertyName == nameof(ViewerViewModel.SelectedSectionName))
        {
            var target = _vm.SelectedSectionName;
            if (SectionCombo.SelectedItem as string != target)
            {
                SectionCombo.SelectedItem = target;
            }
            RefreshItems();
        }
        else if (e.PropertyName == nameof(ViewerViewModel.EditingTitle))
        {
            RefreshSelectedItemDisplayName();
        }
    }

    private void RefreshSections()
    {
        if (_vm == null) return;
        SectionCombo.ItemsSource = _vm.Sections.Select(s => s.Name).ToList();
        SectionCombo.SelectedItem = _vm.SelectedSectionName;
    }

    private void RefreshItems()
    {
        if (_vm == null) return;
        Items.Clear();
        foreach (var r in _vm.VisibleRecordings)
        {
            Items.Add(new RecordingListItem
            {
                Id = r.Id,
                DisplayName = _vm.RecordingDisplayName(r),
                IsChecked = _vm.IsChecked(r.Id),
            });
        }
    }

    private void RefreshSelectedItemDisplayName()
    {
        if (_vm == null || _vm.SelectedRecording is null) return;
        var item = Items.FirstOrDefault(x => x.Id == _vm.SelectedRecording.Id);
        if (item == null) return;
        item.DisplayName = _vm.RecordingDisplayName(_vm.SelectedRecording);
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e) => _vm?.ChooseFolder();

    private void SectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (SectionCombo.SelectedItem is string s && s != _vm.SelectedSectionName)
        {
            _vm.SelectedSectionName = s;
            if (_vm.FolderPath != null)
            {
                _vm.LoadRecordings(_vm.FolderPath, s);
            }
        }
    }

    private void RecordingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (RecordingListBox.SelectedItem is RecordingListItem item)
        {
            _vm.Play(item.Id);
        }
    }

    private void CheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (sender is CheckBox cb && cb.Tag is string id)
        {
            _vm.ToggleChecked(id);
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        _vm?.SelectAllInCurrentSection();
        RefreshItems();
    }

    private void ClearCheckButton_Click(object sender, RoutedEventArgs e)
    {
        _vm?.ClearCheckedInCurrentSection();
        RefreshItems();
    }

    private void DeleteCheckedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (MessageBox.Show("Move selected videos to Recycle Bin?", "Confirm",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _vm.DeleteCheckedRecordings();
        RefreshItems();
    }

    private async void ExportHighlightsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_vm.CheckedRecordingIds.Count == 0)
        {
            MessageBox.Show("Select videos via checkbox first.");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Export Highlights (Selected Videos)",
            FileName = "selected_videos_highlights.mp4",
            Filter = "MP4 (*.mp4)|*.mp4|QuickTime (*.mov)|*.mov",
        };
        if (dlg.ShowDialog() != true) return;
        await _vm.ExportHighlightsFromCheckedAsync(dlg.FileName);
    }
}

public sealed class RecordingListItem
    : INotifyPropertyChanged
{
    public required string Id { get; init; }
    private string _displayName = "";
    private bool _isChecked;

    public required string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged();
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
