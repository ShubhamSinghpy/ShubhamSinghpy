using System.ComponentModel;
using System.Windows;

namespace MediaOrganizer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void ReviewGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var grid = (System.Windows.Controls.DataGrid)sender;
        _vm.SetReviewSelection(grid.SelectedItems.OfType<MediaOrganizer.Core.MediaItem>().ToList());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
