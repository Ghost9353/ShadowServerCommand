using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed class WindowsHeartbeatCollector : IDisposable
{
    private PerformanceCounter? _cpu;
    private PerformanceCounter? _memory;
    private long? _totalMemory;
    public WindowsHeartbeatCollector()
    {
        if(OperatingSystem.IsWindows()) InitializeCounters();
    }
    [SupportedOSPlatform("windows")]
    private void InitializeCounters()
    {
        try { _cpu=new("Processor","% Processor Time","_Total",true);_cpu.NextValue();_memory=new("Memory","Available Bytes",true); }
        catch (Exception ex) when(ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _cpu?.Dispose();_cpu=null;_memory?.Dispose();_memory=null; }
        try
        {
            using var search=new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            using var result=search.Get();
            foreach(ManagementObject item in result) using(item) _totalMemory=checked(Convert.ToInt64(item["TotalVisibleMemorySize"])*1024);
        }
        catch(Exception ex) when(ex is ManagementException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
    public AgentHeartbeat Read(HeartbeatOptions options)
    {
        if(!OperatingSystem.IsWindows()) return new(DateTimeOffset.UtcNow,Environment.TickCount64/1000d,true,null,"Windows process telemetry unavailable on this OS",null,null,null,"0.2.0");
        return ReadWindows(options);
    }
    [SupportedOSPlatform("windows")]
    private AgentHeartbeat ReadWindows(HeartbeatOptions options)
    {
        double? cpu=null;long? available=null;long? working=null;
        try { cpu=_cpu?.NextValue(); available=(long?)_memory?.NextValue(); }
        catch(Exception ex) when(ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        bool? running=null;string evidence="Process identity not configured";
        try
        {
            if(options.MinecraftProcessId is { } pid)
            {
                try
                {
                    using var process=Process.GetProcessById(pid);
                    running=string.Equals(process.MainModule?.FileName,options.MinecraftExecutablePath,StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs((process.StartTime.ToUniversalTime()-options.MinecraftProcessStartedAt!.Value.UtcDateTime).TotalSeconds)<1;
                    working=running is true ? process.WorkingSet64 : null;evidence=running is true?"PID + executable + creation time verified":"PID identity does not match configured Minecraft instance";
                }
                catch(ArgumentException) { running=false;evidence="Configured Minecraft PID absent"; }
            }
            else if(!string.IsNullOrWhiteSpace(options.MinecraftExecutablePath) && !string.IsNullOrWhiteSpace(options.MinecraftCommandLineContains))
            {
                using var search=new ManagementObjectSearcher("SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'");
                using var results=search.Get();var matches=new List<int>();var inaccessible=false;
                foreach(ManagementObject obj in results)
                {
                    using(obj)
                    {
                        var path=obj["ExecutablePath"] as string;var command=obj["CommandLine"] as string;
                        if(path is null || command is null) { inaccessible=true;continue; }
                        if(string.Equals(path,options.MinecraftExecutablePath,StringComparison.OrdinalIgnoreCase) && command.Contains(options.MinecraftCommandLineContains,StringComparison.OrdinalIgnoreCase)) matches.Add(Convert.ToInt32(obj["ProcessId"]));
                    }
                }
                running=matches.Count==1 ? true : matches.Count==0 && !inaccessible ? false : null;
                evidence=matches.Count>1 ? "Ambiguous Minecraft process identity; multiple matches" : inaccessible && matches.Count==0 ? "Process query access insufficient; status unknown" : "Exact executable and command-line marker matched: "+matches.Count;
                if(running is true) { using var process=Process.GetProcessById(matches[0]);working=process.WorkingSet64; }
            }
        }
        catch(Exception ex) when(ex is ManagementException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { running=null;evidence="Process status unavailable: permissions or process changed during query"; }
        return new(DateTimeOffset.UtcNow,Environment.TickCount64/1000d,true,running,evidence,cpu,available,working,"0.2.0",_totalMemory,_totalMemory>0 && available.HasValue ? Math.Clamp(100d*(1-available.Value/(double)_totalMemory.Value),0,100) : null);
    }
    public void Dispose() { if(OperatingSystem.IsWindows()) { _cpu?.Dispose();_memory?.Dispose(); } }
}
