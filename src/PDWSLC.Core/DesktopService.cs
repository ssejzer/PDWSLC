using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PDWSLC.Core;

public sealed record DesktopInfo(string Name, string State, string Image, string? Url,
    string Storage, string Management, string? CreatedAt = null, string? StartedAt = null,
    string? FinishedAt = null);
public sealed record StorageInfo(string Name, string? Desktop, string Management,
    string Containers, bool Orphaned)
{
    public bool IsAttachedTo(string desktopName) => !Orphaned &&
        Containers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(desktopName, StringComparer.Ordinal);
}
public sealed record DesktopRequest(string Action, string? Name = null, string? Session = null,
    string? Image = null, int? Port = null, string? TimeZone = null,
    bool IncludeStorage = false, bool Adopt = false);
public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public void EnsureSuccess()
    {
        if (ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(Error)
                ? $"Desktop command failed ({ExitCode}). {Output}" : Error.Trim());
    }
}

// All lifecycle and storage policy remains in the bundled PowerShell manager.
// Arguments are passed directly to powershell.exe; no command shell is involved.
public sealed class DesktopService(string bridgePath)
{
    private static readonly HashSet<string> Actions =
        ["List", "Storage", "Start", "Open", "Stop", "Delete", "Status", "Logs"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<string> BuildArguments(string bridgePath, DesktopRequest request)
    {
        if (!Actions.Contains(request.Action)) throw new ArgumentException("Unknown desktop action.");
        if (request.Name is not null && !Regex.IsMatch(request.Name, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z"))
            throw new ArgumentException("Use letters, digits, dots, underscores or hyphens for the desktop name.");
        if (request.Action is not ("List" or "Storage") && string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Select or enter a desktop name.");
        if (request.Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (request.IncludeStorage && request.Action != "Delete") throw new ArgumentException("Storage removal requires Delete.");
        if (request.Adopt && !request.IncludeStorage) throw new ArgumentException("Legacy storage authorization requires storage removal.");
        List<string> args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", bridgePath, "-Action", request.Action];
        if (request.Action is "List" or "Storage" or "Status") args.Add("-Json");
        Add("-Name", request.Name);
        Add("-Session", request.Session);
        Add("-Image", request.Image);
        if (request.Port.HasValue) Add("-Port", request.Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("-TimeZone", request.TimeZone);
        if (request.IncludeStorage) args.Add("-IncludeStorage");
        if (request.Adopt) args.Add("-Adopt");
        return args;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { args.Add(key); args.Add(value); }
        }
    }

    public static IReadOnlyList<T> ReadRows<T>(string json) =>
        JsonSerializer.Deserialize<List<T>>(json.Trim(), JsonOptions)
        ?? throw new InvalidOperationException("The desktop manager returned null instead of a list.");

    public async Task<CommandResult> RunAsync(DesktopRequest request, Action<string>? report = null)
    {
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The bundled desktop manager is missing.", bridgePath);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo start = new(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // Avoid cmd.exe's UNC working-directory fallback.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        foreach (string argument in BuildArguments(bridgePath, request)) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        process.Start();
        Task<string> output = DrainAsync(process.StandardOutput, report);
        Task<string> error = DrainAsync(process.StandardError, report);
        // Bound read-only discovery; do not interrupt container creation or deletion.
        using CancellationTokenSource timeout = new();
        if (request.Action is "List" or "Storage" or "Status" or "Logs")
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw new TimeoutException("WSLC discovery or logs did not finish within 60 seconds. Check the runtime and refresh.");
        }
        return new(process.ExitCode, await output, await error);
    }

    private static async Task<string> DrainAsync(StreamReader reader, Action<string>? report)
    {
        StringBuilder buffer = new();
        while (await reader.ReadLineAsync() is { } line)
        {
            buffer.AppendLine(line);
            report?.Invoke(line);
        }
        return buffer.ToString();
    }
}
