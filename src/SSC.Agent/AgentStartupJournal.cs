using System.Text.Json;
using SSC.Infrastructure;
namespace SSC.Agent;

// Independent of DI, Serilog and the Windows Event Log provider: usable before Host.Build.
public sealed class AgentStartupJournal
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    public void Write(string stage, Exception? error = null)
    {
        var record = JsonSerializer.Serialize(new { TimestampUtc=DateTimeOffset.UtcNow, Stage=stage,
            ProcessId=Environment.ProcessId, Account=OperatingSystem.IsWindows()?System.Security.Principal.WindowsIdentity.GetCurrent().Name:"non-Windows", ElapsedMs=_clock.ElapsedMilliseconds,
            Build=typeof(AgentStartupJournal).Module.ModuleVersionId,
            ErrorType=error?.GetType().Name, HResult=error is null ? null : $"0x{error.HResult:X8}" });
        try
        {
            lock(_gate)
            {
                var directory=Path.Combine(ServicePaths.Root,"Agent","logs");Directory.CreateDirectory(directory);
                var path=Path.Combine(directory,"startup-"+DateTime.UtcNow.ToString("yyyyMMdd")+".jsonl");
                if(File.Exists(path) && new FileInfo(path).Length>=1024*1024)File.Move(path,Path.Combine(directory,"startup-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".jsonl"));
                using(var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite))
                using(var writer=new StreamWriter(stream)) { writer.WriteLine(record);writer.Flush();stream.Flush(true); }
                foreach(var old in Directory.EnumerateFiles(directory,"startup-*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Skip(14))File.Delete(old);
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine(record); } // No exception message or secret/path is emitted.
    }
}
