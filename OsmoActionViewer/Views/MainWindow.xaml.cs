using System;
using System.Windows;
using System.Windows.Input;
using OsmoActionViewer.ViewModels;

namespace OsmoActionViewer.Views;

public partial class MainWindow : Window
{
    public ViewerViewModel Vm => (ViewerViewModel)Resources["Vm"];
    private bool _isPlayerFullscreen;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private Thickness _previousRootMargin;
    private Thickness _previousLibraryMargin;
    private Thickness _previousDetailMargin;
    private double _previousMediaHeight = 420;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm.RestoreLastOpenedFolderIfAvailable();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                DetailView.SeekRelative(-10);
                e.Handled = true;
                break;
            case Key.Right:
                DetailView.SeekRelative(10);
                e.Handled = true;
                break;
            case Key.Space:
                DetailView.TogglePlayPause();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isPlayerFullscreen)
                {
                    TogglePlayerFullscreen();
                    e.Handled = true;
                }
                break;
        }
    }

    public void TogglePlayerFullscreen()
    {
        if (_isPlayerFullscreen)
        {
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            RootGrid.Margin = _previousRootMargin;
            LibraryPanel.Visibility = Visibility.Visible;
            LibraryPanel.Margin = _previousLibraryMargin;
            DetailPanel.Margin = _previousDetailMargin;
            LibraryColumn.Width = new GridLength(420);
            DetailView.SetFullscreenVisualState(false, _previousMediaHeight);
            _isPlayerFullscreen = false;
            return;
        }

        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        _previousRootMargin = RootGrid.Margin;
        _previousLibraryMargin = LibraryPanel.Margin;
        _previousDetailMargin = DetailPanel.Margin;
        _previousMediaHeight = DetailView.Media.Height;

        LibraryPanel.Visibility = Visibility.Collapsed;
        LibraryColumn.Width = new GridLength(0);
        RootGrid.Margin = new Thickness(0);
        DetailPanel.Margin = new Thickness(0);
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        var fullscreenPlayerHeight = Math.Max(240, SystemParameters.PrimaryScreenHeight - 160);
        DetailView.SetFullscreenVisualState(true, fullscreenPlayerHeight);
        _isPlayerFullscreen = true;
    }
}
