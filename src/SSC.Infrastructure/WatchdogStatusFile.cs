using System.Text.Json;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed class WatchdogStatusFile(string path) : IWatchdogStatusSource
{
    public async Task<WatchdogDashboard?> ReadAsync(CancellationToken ct=default)
    {
        if(!File.Exists(path))return null;
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<WatchdogDashboard>(file,cancellationToken:ct);
    }
    public async Task WriteAsync(WatchdogDashboard status,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+".tmp";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(status),ct);File.Move(temporary,path,true);
    }
}
