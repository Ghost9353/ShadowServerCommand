using System.Windows;
using System.Windows.Input;
namespace SSC.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly System.Windows.Threading.DispatcherTimer _watchdogTimer = new() { Interval=TimeSpan.FromSeconds(5) };
    public MainWindow(MainViewModel model)
    {
        InitializeComponent(); _model=model; DataContext=model;
        Loaded += async (_,_) => { await _model.InitializeAsync(); CommandInput.Focus(); _watchdogTimer.Start(); };
        _watchdogTimer.Tick += async (_,_) => await _model.RefreshWatchdogAsync();
        Closed += (_,_) => _watchdogTimer.Stop();
    }
    private async void OnCommandKeyDown(object sender, KeyEventArgs e)
    { if(e.Key==Key.Enter) { e.Handled=true; await _model.ExecuteAsync(); } }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key=e.Key==Key.System ? e.SystemKey : e.Key;
        if(Keyboard.Modifiers==ModifierKeys.Alt && key>=Key.D0 && key<=Key.D9)
        { _model.Navigate(key==Key.D0 ? 9 : (int)key-(int)Key.D1); e.Handled=true; }
        else if(Keyboard.Modifiers==ModifierKeys.Control && key==Key.W) { _model.SelectedModule=_model.Navigation.First(n=>n.Definition.Id=="watchdog"); e.Handled=true; }
        else if(Keyboard.Modifiers==ModifierKeys.Control && key==Key.L) { CommandInput.Focus(); e.Handled=true; }
        else if(key==Key.F1) { _model.ShowHelp(); e.Handled=true; }
        else if(key==Key.F5) { await _model.RefreshAsync(); await _model.RefreshWatchdogAsync(); e.Handled=true; }
    }
}
