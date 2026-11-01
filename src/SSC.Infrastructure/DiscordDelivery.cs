using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed record DiscordOptions
{
    public string WebhookSecret { get; init; } = "discord-webhook";
    public int RetryBurst { get; init; } = 8;
    public int CooldownSeconds { get; init; } = 1800;
}
public sealed class DiscordDelivery(WatchdogRepository repository, HttpClient http, ISecretStore secrets, DiscordOptions options, ILogger<DiscordDelivery>? logger = null)
{
    public static bool IsDiscordWebhook(Uri uri) => uri.Scheme=="https" && uri.Host=="discord.com" && uri.Port==443 &&
        uri.AbsolutePath.StartsWith("/api/webhooks/",StringComparison.Ordinal) && uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries).Length==4 && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);
    // One attempt per call; queue scheduling prevents blocking the 15-second monitoring worker.
    public async Task<bool> PumpAsync(DateTimeOffset now,CancellationToken ct)
    {
        var item=repository.NextAlert(now);if(item is null)return false;
        var attempts=item.Attempts+1;
        try
        {
            var secret=secrets.Read(options.WebhookSecret);
            if(!Uri.TryCreate(secret,UriKind.Absolute,out var uri) || !IsDiscordWebhook(uri))
            { logger?.LogWarning("Discord alert {AlertId} held; invalid URI or rejected credentials/payload",item.Alert.Id);repository.DeliveryResult(item.Alert.Id,attempts,"Held",null,null);return false; }
            // Disable mentions, include stable ID and original event time for delayed/ambiguous deliveries.
            var message=item.Alert.Message+ $"\nEVENT ID: {item.Alert.Id}\nEVENT TIME: {item.Alert.CreatedAt:O}";
            using var response=await http.PostAsJsonAsync(uri,new { content=message.Length>1950?message[..1950]:message,allowed_mentions=new { parse=Array.Empty<string>() } },ct);
            if(response.IsSuccessStatusCode)
            { logger?.LogInformation("Discord alert {AlertId} acknowledged on attempt {Attempt}",item.Alert.Id,attempts);repository.DeliveryResult(item.Alert.Id,attempts,"Delivered",null,DateTimeOffset.UtcNow);return true; }
            if(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            { logger?.LogWarning("Discord alert {AlertId} held; invalid URI or rejected credentials/payload",item.Alert.Id);repository.DeliveryResult(item.Alert.Id,attempts,"Held",null,null);return false; }
            logger?.LogWarning("Discord alert {AlertId} received HTTP {StatusCode}; retry scheduled",item.Alert.Id,(int)response.StatusCode);
            var delay=RetryDelay(attempts);
            if(response.StatusCode==HttpStatusCode.TooManyRequests)
            {
                var retry=response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date-now);
                // Discord also returns retry_after seconds in JSON, including global limits.
                try
                {
                    await response.Content.LoadIntoBufferAsync(16384);
                    using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    if(json.RootElement.TryGetProperty("retry_after",out var value) && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds>=0)
                        { var bodyRetry=TimeSpan.FromSeconds(Math.Min(seconds,86400)); if(retry is null || bodyRetry>retry)retry=bodyRetry; }
                }
                catch(Exception ex) when(ex is System.Text.Json.JsonException or HttpRequestException) { }
                if(retry>delay)delay=retry.Value;
            }
            repository.DeliveryResult(item.Alert.Id,attempts,"Retry",now+delay,null);return false;
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or IOException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            // Never log exception text: HttpRequestException may contain the secret URL.
            logger?.LogWarning("Discord alert {AlertId} deferred ({ErrorType})",item.Alert.Id,ex.GetType().Name);
            repository.DeliveryResult(item.Alert.Id,attempts,"Retry",now+RetryDelay(attempts),null);return false;
        }
    }
    private TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(attempts % Math.Max(1,options.RetryBurst)==0
        ? Math.Max(300,options.CooldownSeconds) : Math.Min(300,5*Math.Pow(2,Math.Min(attempts-1,6))));
}
