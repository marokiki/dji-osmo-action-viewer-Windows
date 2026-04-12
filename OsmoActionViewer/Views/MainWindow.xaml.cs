using System.Windows;
using System.Windows.Input;
using OsmoActionViewer.ViewModels;

namespace OsmoActionViewer.Views;

public partial class MainWindow : Window
{
    public ViewerViewModel Vm => (ViewerViewModel)Resources["Vm"];

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
        }
    }
}
