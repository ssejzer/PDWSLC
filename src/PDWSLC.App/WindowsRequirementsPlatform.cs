using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PDWSLC.Core;

namespace PDWSLC.App;

internal sealed class WindowsRequirementsPlatform : IRequirementsPlatform
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    private static string RestartMarker => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDWSLC", "setup-restart.json");
    private string? bootIdentity;
    private sealed record RestartState(string? BootIdentity, long UptimeMilliseconds);

    private static ProcessStartInfo Command(string mode, bool elevated)
    {
        if (!File.Exists(PowerShellPath)) throw new InvalidOperationException("Windows PowerShell is missing. Restore Windows system tools before setup can run.");
        var info = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = elevated,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };
        if (elevated) { info.Verb = "runas"; info.WindowStyle = ProcessWindowStyle.Normal; }
        else
        {
            info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", BundledRuntime.GetScriptPath("Requirements.ps1"), "-Mode", mode }) info.ArgumentList.Add(argument);
        return info;
    }

    public async Task<RequirementSnapshot> ProbeAsync()
    {
        using var process = Process.Start(Command("Check", false)) ?? throw new InvalidOperationException("Could not start pre-flight.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(output, error);
            throw new TimeoutException("Pre-flight timed out. Check Windows/WSL and retry from Host > Check requirements.");
        }
        var result = new CommandResult(process.ExitCode, await output, await error); result.EnsureSuccess();
        var snapshot = JsonSerializer.Deserialize<RequirementSnapshot>(result.Output, JsonOptions)
            ?? throw new InvalidDataException("Pre-flight returned no result.");
        bootIdentity = snapshot.BootIdentity;
        return snapshot with { RestartPending = RestartStillPending(snapshot.BootIdentity) };
    }

    // Prefer the OS boot identity; decreasing uptime is a conservative fallback.
    private static bool RestartStillPending(string? currentBootIdentity)
    {
        if (!File.Exists(RestartMarker)) return false;
        var marker = JsonSerializer.Deserialize<RestartState>(File.ReadAllText(RestartMarker))
            ?? throw new InvalidDataException("Setup restart marker is invalid.");
        bool restarted = marker.BootIdentity is not null && currentBootIdentity is not null
            ? marker.BootIdentity != currentBootIdentity : Environment.TickCount64 < marker.UptimeMilliseconds;
        if (!restarted) return true;
        File.Delete(RestartMarker); return false;
    }

    public async Task<RequirementInstallResult> InstallAsync(bool installWsl, Action<string> report)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDWSLC", "setup");
        Directory.CreateDirectory(directory);
        var resultPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var command = Command(installWsl ? "Install" : "Update", true);
            command.ArgumentList.Add("-ResultPath"); command.ArgumentList.Add(resultPath);
            report("The Windows administrator prompt belongs to prerequisite setup. Progress is shown in its setup console.");
            using var process = Process.Start(command) ?? throw new InvalidOperationException("Could not start Windows prerequisite setup.");
            // Do not kill an installer mid-operation or time out a system feature change.
            await process.WaitForExitAsync();
            if (!File.Exists(resultPath)) return new(false, false, $"Setup returned no result (exit code {process.ExitCode}).");
            var result = JsonSerializer.Deserialize<RequirementInstallResult>(File.ReadAllText(resultPath), JsonOptions)
                ?? throw new InvalidDataException("Invalid prerequisite setup result.");
            if (process.ExitCode != 0 && result.Succeeded) return new(false, false, $"Setup failed (exit code {process.ExitCode}).");
            if (result.RestartRequired)
            {
                var marker = new RestartState(bootIdentity, Environment.TickCount64);
                var temporary = RestartMarker + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(marker));
                File.Move(temporary, RestartMarker, overwrite: true);
            }
            report(result.RestartRequired ? "Windows reports a restart is required." : "WSL setup finished; checking the installed tools again.");
            return result;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new(false, false, "Windows administrator consent was declined. Setup did not run. Use Host > Check requirements to retry."); }
        finally { if (File.Exists(resultPath)) File.Delete(resultPath); }
    }
}
