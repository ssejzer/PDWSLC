namespace PDWSLC.Core;

public sealed record RequirementSnapshot(bool WindowsSupported, bool PowerShellSupported,
    bool CurlAvailable, string? WslVersion, bool WslcAvailable,
    bool? VirtualMachinePlatformEnabled, bool? FirmwareVirtualizationAvailable, bool RestartPending = false, string? BootIdentity = null)
{
    public Version? ParsedWslVersion => Version.TryParse(WslVersion, out var version) ? version : null;
    public bool WslInstalled => ParsedWslVersion is not null;
    public bool NeedsInstall => !WslInstalled || VirtualMachinePlatformEnabled == false;
    public bool NeedsUpdate => WslInstalled && (ParsedWslVersion! < new Version(3, 0, 1) || !WslcAvailable);
    public bool Ready => WindowsSupported && PowerShellSupported && CurlAvailable &&
        FirmwareVirtualizationAvailable != false && !RestartPending && !NeedsInstall && !NeedsUpdate;
    public string? ManualIssue => !WindowsSupported ? "Windows 10 build 19041 or newer is required. Update Windows before continuing."
        : !PowerShellSupported ? "Windows PowerShell 5.1 is missing or disabled. Restore it before continuing."
        : !CurlAvailable ? "Windows curl.exe is missing. Restore Windows system tools before continuing."
        : FirmwareVirtualizationAvailable == false ? "Enable CPU virtualization in firmware (or nested virtualization for this VM), then restart Windows."
        : null;
}
public sealed record RequirementInstallResult(bool Succeeded, bool RestartRequired, string? Error = null);
public interface IRequirementsPlatform
{
    Task<RequirementSnapshot> ProbeAsync();
    Task<RequirementInstallResult> InstallAsync(bool installWsl, Action<string> report);
}

public sealed class RequirementsCoordinator(IRequirementsPlatform platform)
{
    public async Task<RequirementSnapshot> EnsureAsync(bool allowInstall, Action<string> report)
    {
        report("Pre-flight: .NET is bundled in PDWSLC.exe; no .NET installation is needed.");
        var snapshot = await platform.ProbeAsync();
        report($"Pre-flight: WSL {snapshot.WslVersion ?? "not installed"}; WSLC {(snapshot.WslcAvailable ? "available" : "missing")}.");
        if (snapshot.ManualIssue is { } manual) throw new InvalidOperationException(manual);
        if (snapshot.RestartPending) throw new InvalidOperationException("Restart Windows to finish prerequisite setup, then reopen PDWSLC. No desktops were accessed.");
        if (snapshot.Ready) { report("Pre-flight passed."); return snapshot; }
        if (!allowInstall) throw new InvalidOperationException("Requirements are still missing. Right-click Host and choose Check requirements to retry setup.");
        report(snapshot.NeedsInstall ? "Installing WSL and required Windows components. Windows may request administrator consent."
            : "Updating WSL to obtain WSL 3.0.1+ and WSLC. Windows may request administrator consent.");
        var result = await platform.InstallAsync(snapshot.NeedsInstall, report);
        if (!result.Succeeded) throw new InvalidOperationException(result.Error ?? "Requirement installation failed. Check the setup output and retry.");
        if (result.RestartRequired) throw new InvalidOperationException("Restart Windows to finish prerequisite setup, then reopen PDWSLC. Restart is not automatic.");
        // Never assume the installer's exit code proves prerequisites are usable.
        snapshot = await platform.ProbeAsync();
        if (!snapshot.Ready) throw new InvalidOperationException(snapshot.ManualIssue ?? "Setup finished but requirements are not ready. A Windows restart may be needed; reopen PDWSLC afterwards.");
        report("Pre-flight passed after setup.");
        return snapshot;
    }
}
