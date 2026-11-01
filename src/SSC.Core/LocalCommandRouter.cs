namespace SSC.Core;
public enum LocalCommandKind { Help, Navigate, Refresh, Clear, Crt, Invalid }
public sealed record LocalCommand(LocalCommandKind Kind, string Argument = "");
public static class LocalCommandRouter
{
    public static LocalCommand Parse(string? input)
    {
        var parts = (input ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new(LocalCommandKind.Invalid);
        var verb = parts[0].ToLowerInvariant();
        var arg = parts.Length == 2 ? parts[1].Trim().ToLowerInvariant() : "";
        return (verb, arg) switch
        {
            ("help", "") => new(LocalCommandKind.Help),
            ("refresh", "") => new(LocalCommandKind.Refresh),
            ("clear", "") => new(LocalCommandKind.Clear),
            ("open", _) when Modules.All.Any(m => m.Id == arg) => new(LocalCommandKind.Navigate, arg),
            ("crt", "on" or "off") => new(LocalCommandKind.Crt, arg),
            _ => new(LocalCommandKind.Invalid)
        };
    }
}
