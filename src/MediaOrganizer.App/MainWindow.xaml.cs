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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }
}
