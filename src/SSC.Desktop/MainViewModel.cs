using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SSC.Core;
namespace SSC.Desktop;
public sealed record NavigationItem(int Index, ModuleDefinition Definition)
{
    public string Label => Index == 10 ? "W  WATCHDOG" : $"{(Index + 1) % 10:0}  {Definition.Id.ToUpperInvariant()}";
}
public sealed class MainViewModel(ITelemetrySource source, IMetricStore store, ILogger<MainViewModel> logger, IWatchdogStatusSource? watchdog = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public IReadOnlyList<NavigationItem> Navigation { get; } = Modules.All.Select((m,i) => new NavigationItem(i,m)).ToArray();
    private NavigationItem? _selected;
    public NavigationItem SelectedModule { get => _selected ?? Navigation[0]; set { _selected=value; Search=""; Notify(); Notify(nameof(Rows)); Notify(nameof(Column1)); Notify(nameof(Column2)); Notify(nameof(Column3)); Notify(nameof(IsWatchdog)); Notify(nameof(ShowMetrics)); } }
    public string Column1 => SelectedModule.Definition.Columns[0];
    public string Column2 => SelectedModule.Definition.Columns[1];
    public string Column3 => SelectedModule.Definition.Columns[2];
    private string _search="";
    public string Search { get=>_search; set { _search=value; Notify(); Notify(nameof(Rows)); } }
    public bool IsWatchdog => SelectedModule.Definition.Id == "watchdog";
    public bool ShowMetrics => !IsWatchdog;
    public IEnumerable<string[]> Rows => (IsWatchdog ? WatchdogRows : SelectedModule.Definition.Rows).Where(r => string.IsNullOrWhiteSpace(Search) || r.Any(c => c.Contains(Search,StringComparison.OrdinalIgnoreCase)));
    private WatchdogDashboard? _watchdogStatus;
    private string _watchdogReadError = "Status file unavailable; run SSC.Watchdog on AM5";
    public bool WatchdogCritical => _watchdogStatus?.State.Computer == ComputerCondition.Unreachable || _watchdogStatus?.State.Minecraft is MinecraftCondition.ProcessStopped or MinecraftCondition.PotentiallyUnresponsive;
    public bool WatchdogDegraded => _watchdogStatus is null || _watchdogStatus.State.Failures > 0 ||
        _watchdogStatus.State.Computer != ComputerCondition.Online || _watchdogStatus.State.Minecraft != MinecraftCondition.Running ||
        DateTimeOffset.UtcNow - _watchdogStatus.WrittenAt > TimeSpan.FromSeconds(45) || _watchdogStatus.ServiceHealth != "RUNNING";
    public async Task RefreshWatchdogAsync()
    {
        try { _watchdogStatus = watchdog is null ? null : await watchdog.ReadAsync(); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _watchdogStatus=null; _watchdogReadError="Status file unreadable; check AM5 service permissions/logs"; }
        Notify(nameof(WatchdogCritical));Notify(nameof(WatchdogDegraded));Notify(nameof(Rows));
    }
    private IEnumerable<string[]> WatchdogRows
    {
        get
        {
            var dashboard=_watchdogStatus;
            if(dashboard is null) return [new[]{"WATCHDOG", "UNAVAILABLE", _watchdogReadError}];
            var state=dashboard.State;
            var stale=DateTimeOffset.UtcNow-dashboard.WrittenAt>TimeSpan.FromSeconds(45);
            var active=state.Incidents.FirstOrDefault(i=>i.Id==state.ComputerIncident);
            var rows=new List<string[]>
            {
                new[]{"AM4 computer",stale?"UNKNOWN / STALE":state.Computer.ToString(),state.LastEvidence},
                new[]{"Minecraft process",stale || state.Failures>0 ? "UNKNOWN (last confirmed: "+state.Minecraft+")" : state.Minecraft.ToString(),"Port acceptance does not prove healthy ticks"},
                new[]{"Last good heartbeat",state.LastHeartbeat?.ToString("O")??"NEVER", "UTC"},
                new[]{"Heartbeat latency",state.HeartbeatLatencyMs?.ToString("0.0")+" ms",state.HeartbeatLatencyMs.HasValue?"Measured HTTPS exchange":"UNAVAILABLE"},
                new[]{"Consecutive failures",state.Failures.ToString(),"Three failures declare an incident; three successes confirm recovery"},
                new[]{"Current outage elapsed",active is null?"NONE":(DateTimeOffset.UtcNow-active.StartedAt).ToString("c"),active?.ObservationInterrupted is true?"Includes unobserved interval; continuity interrupted":"Measured from first failed observation"},
                new[]{"Discord delivery",dashboard.DeliveryStatus,$"Queued: {dashboard.PendingAlerts}; held: {dashboard.HeldAlerts}; last delivered: {dashboard.LastDelivered?.ToString("O")??"NEVER"}"},
                new[]{"Watchdog service",stale?"STALE / CHECK SERVICE":dashboard.ServiceHealth,"AM5 snapshot: "+dashboard.WrittenAt.ToString("O")},
                new[]{"Monitoring gaps",state.Gaps.Count.ToString(),"Recent gaps; durable archive retains all gaps"},
                new[]{"AM4 uptime",state.LastAgentHeartbeat?.UptimeSeconds.ToString("0")+" seconds","Last authenticated agent measurement"},
                new[]{"AM4 CPU / memory",$"CPU {state.LastAgentHeartbeat?.CpuPercent?.ToString("0.0")??"UNAVAILABLE"}% / memory {state.LastAgentHeartbeat?.MemoryUsedPercent?.ToString("0.0")??"UNAVAILABLE"}%","Host physical memory; Java heap remains unavailable"}
            };
            rows.AddRange(state.Incidents.TakeLast(10).Reverse().Select(i=>new[]{i.Kind,i.RecoveredAt.HasValue?"RECOVERED":"OPEN",$"{i.StartedAt:O} -> {i.RecoveredAt?.ToString("O")??"ongoing"}; continuity {(i.ObservationInterrupted?"interrupted":"sampled")}"}));
            return rows;
        }
    }
    public ObservableCollection<Metric> Metrics { get; } = [];
    public string CommandText { get; set; } = "";
    private string _output="SSC-MC / BOOT ROM v0.1.0\n[OK] MVVM console initialized\n[SAFE] Live adapters disabled; simulation fixtures selected\n[WAIT] Initializing SQLite archive...";
    public string TerminalOutput { get=>_output; private set { _output=value; Notify(); } }
    private string _status="BOOTING / LOCAL SIMULATION";
    public string Status { get=>_status; private set { _status=value; Notify(); } }
    private string _history="";
    public string HistorySummary { get=>_history; private set { _history=value; Notify(); } }
    private bool _crt;
    public bool CrtEnabled { get=>_crt; private set { _crt=value; Notify(); } }
    private bool _busy;
    public async Task InitializeAsync() { await RefreshAsync(); await RefreshWatchdogAsync(); }
    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy=true;
        try
        {
            await store.InitializeAsync();
            var snapshot = await source.ReadAsync(CancellationToken.None);
            await store.AppendAsync(snapshot);
            var history = await store.ReadRecentAsync(100);
            Metrics.Clear(); foreach(var metric in snapshot.Metrics) Metrics.Add(metric);
            Status=$"SIMULATED // sampled {snapshot.Timestamp:HH:mm:ss} UTC // SQLite archive ready // CRT {(CrtEnabled ? "ON" : "OFF")}";
            HistorySummary=$"ARCHIVE: {history.Count} most recent simulated samples loaded (maximum 100); historical records retained across restarts.";
            Write("[OK] Simulated snapshot persisted. Type help for local commands.");
            logger.LogInformation("Persisted {Origin} snapshot at {Timestamp}",snapshot.Origin,snapshot.Timestamp);
        }
        catch(Exception ex)
        {
            Status="ERROR / ARCHIVE OR TELEMETRY UNAVAILABLE — F5 to retry";
            Write("[ERROR] Cannot initialize or write local archive. Check disk access and logs; F5 retries. No server action taken.");
            logger.LogError(ex,"Desktop sample/archive failed");
        }
        finally { _busy=false; }
    }
    private void Write(string text) => TerminalOutput=string.Join('\n',(TerminalOutput+"\n"+text).Split('\n').TakeLast(100));
    public void Navigate(int index) { if(index>=0 && index<Navigation.Count) SelectedModule=Navigation[index]; }
    public async Task ExecuteAsync()
    {
        var input=CommandText; CommandText=""; Notify(nameof(CommandText));
        Write("SSC> "+input);
        var command=LocalCommandRouter.Parse(input);
        switch(command.Kind)
        {
            case LocalCommandKind.Help: ShowHelp(); break;
            case LocalCommandKind.Navigate: SelectedModule=Navigation.First(m=>m.Definition.Id==command.Argument); Write("[LOCAL] Module selected: "+command.Argument); break;
            case LocalCommandKind.Refresh: await RefreshAsync(); await RefreshWatchdogAsync(); break;
            case LocalCommandKind.Clear: TerminalOutput=""; break;
            case LocalCommandKind.Crt: CrtEnabled=command.Argument=="on"; Write("[LOCAL] CRT "+command.Argument); break;
            default: Write("[REJECTED] Unknown local command. Type help. Live server commands are disabled."); break;
        }
    }
    public void ShowHelp() => Write("help | open <module> | refresh | clear | crt on/off\nModules: "+string.Join(", ",Navigation.Select(n=>n.Definition.Id))+"\nAlt+1..0 navigate; Ctrl+W watchdog; Ctrl+L terminal; F5 sample; application close affects desktop only.");
}
