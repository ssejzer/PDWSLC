using PDWSLC.Core;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    bool rejected = false;
    try { action(); } catch (ArgumentException) { rejected = true; }
    Assert(rejected, "Invalid operation was accepted.");
}
const string bridge = @"\\wsl.localhost\Ubuntu\a folder\Invoke-Desktop.ps1";
var arguments = DesktopService.BuildArguments(bridge, new("Start", "desktop", "session with spaces", "image; $(bad)", 3001));
Assert(arguments.Contains(bridge) && arguments.Contains("session with spaces") && arguments.Contains("image; $(bad)"), "Arguments were split or escaped into command text.");
Assert(!arguments.Contains("-Command"), "Runtime invoked a command shell.");
Assert(DesktopService.BuildArguments(bridge, new("List")).Contains("-Json"), "Listing must return structured output.");
Reject(() => DesktopService.BuildArguments(bridge, new("Delete")));
Reject(() => DesktopService.BuildArguments(bridge, new("Delete", "bad;name")));
Reject(() => DesktopService.BuildArguments(bridge, new("Start", "desktop", Port: 0)));
Reject(() => DesktopService.BuildArguments(bridge, new("Stop", "desktop", IncludeStorage: true)));
Reject(() => DesktopService.BuildArguments(bridge, new("Delete", "desktop", Adopt: true)));
var deletion = DesktopService.BuildArguments(bridge, new("Delete", "desktop", IncludeStorage: true, Adopt: true));
Assert(deletion.Contains("-IncludeStorage") && deletion.Contains("-Adopt"), "Explicit deletion options were lost.");
Assert(!DesktopService.BuildArguments(bridge, new("Delete", "desktop")).Contains("-IncludeStorage"), "Container deletion must preserve storage by default.");
var desktops = DesktopService.ReadRows<DesktopInfo>("[{\"Name\":\"desktop\",\"State\":\"running\",\"Image\":\"webtop\",\"Url\":null,\"Storage\":\"desktop-home\",\"Management\":\"Legacy\"}]");
Assert(desktops.Count == 1 && desktops[0].Management == "Legacy", "Desktop JSON was not decoded.");
Assert(DesktopService.ReadRows<DesktopInfo>("[]").Count == 0, "Empty desktop list failed.");
var storage = DesktopService.ReadRows<StorageInfo>("[{\"Name\":\"desktop-home\",\"Desktop\":null,\"Management\":\"Legacy candidate\",\"Containers\":\"\",\"Orphaned\":true}]");
Assert(storage[0].Orphaned && storage[0].Desktop is null, "Legacy orphan storage was not decoded.");
bool failureReported = false;
try { new CommandResult(1, "", "storage retained").EnsureSuccess(); }
catch (InvalidOperationException error) { failureReported = error.Message == "storage retained"; }
Assert(failureReported, "Runtime failure was lost.");
Console.WriteLine("PASS: safe process arguments, explicit deletion flags, validation, desktop/storage JSON and runtime errors");

var directory = Path.Combine(Path.GetTempPath(), "pdwslc-folders-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var file = Path.Combine(directory, "folders.json");
    var folders = new FolderStore(file);
    var work = folders.AddFolder("", FolderStore.HostId, "Work");
    var child = folders.AddFolder("", work, "Projects");
    var desktop = new DesktopInfo("desktop", "running", "webtop", null, "desktop-home", "Managed");
    var home = new StorageInfo("desktop-home", "desktop", "Managed", "", true);
    folders.MoveDesktop("", desktop, child);
    folders.Reconcile("", [], [home]);
    Assert(folders.StorageLocation("", home.Name) == child, "Orphan storage lost its last desktop folder.");
    folders = new FolderStore(file);
    Assert(folders.DesktopLocation("", desktop.Name) == child, "Folder placement was not persisted.");
    var elsewhere = folders.AddFolder("", FolderStore.HostId, "Personal");
    folders.MoveStorage("", home, elsewhere);
    folders.Reconcile("", [desktop], [home with { Orphaned = false }]);
    Assert(folders.DesktopLocation("", desktop.Name) == elsewhere, "Recreated desktop did not follow moved orphan storage.");
    folders.ForgetDeleted("", desktop.Name, false);
    folders.Reconcile("", [], [home]);
    Assert(folders.StorageLocation("", home.Name) == elsewhere, "Container deletion changed retained storage placement.");
    Assert(folders.Get("other-session").Folders.Count == 1, "Session folders leaked into another session.");
    bool hostProtected = false;
    try { folders.DeleteFolder("", FolderStore.HostId); } catch (InvalidOperationException) { hostProtected = true; }
    Assert(hostProtected, "Host was deleted.");
    hostProtected = false;
    try { folders.RenameFolder("", FolderStore.HostId, "Other"); } catch (InvalidOperationException) { hostProtected = true; }
    Assert(hostProtected, "Host was renamed.");
    Reject(() => folders.AddFolder("", FolderStore.HostId, "personal"));
    folders.DeleteFolder("", elsewhere);
    Assert(folders.StorageLocation("", home.Name) == FolderStore.HostId, "Deleting a folder lost its items.");
    folders.DeleteFolder("", work);
    Assert(folders.Get("").Folders.Single(f => f.Id == child).ParentId == FolderStore.HostId, "Deleting a folder lost its subfolders.");
    folders.RenameFolder("", child, "Projects renamed");
    Assert(FolderStore.FolderPath(folders.Get(""), child) == "Host / Projects renamed", "Folder rename/path failed.");
    File.WriteAllText(file, "{invalid");
    bool corruptRejected = false;
    try { _ = new FolderStore(file); } catch (System.Text.Json.JsonException) { corruptRejected = true; }
    Assert(corruptRejected && File.ReadAllText(file) == "{invalid", "Invalid folder metadata was overwritten.");
    Console.WriteLine("PASS: Host protection, nested folders, moves, orphan placement, recreation, persistence, session isolation, folder removal and corrupt metadata preservation");
}
finally { Directory.Delete(directory, recursive: true); }

var failedSaveDirectory = Path.Combine(Path.GetTempPath(), "pdwslc-save-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(failedSaveDirectory);
try
{
    var store = new FolderStore(failedSaveDirectory);
    bool rejected = false;
    try { store.AddFolder("", FolderStore.HostId, "Unsaved"); } catch (IOException) { rejected = true; }
    Assert(rejected && store.Get("").Folders.Count == 1, "Failed persistence changed the in-memory folder tree.");
    rejected = false;
    try { store.MoveStorage("", new("home", null, "Managed", "other", false), FolderStore.HostId); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Attached storage was moved independently.");
    Console.WriteLine("PASS: failed-save rollback and attached-storage move refusal");
}
finally { Directory.Delete(failedSaveDirectory, recursive: true); }

var attached = new StorageInfo("desktop-home", "desktop", "Managed", "desktop, desktop1", false);
Assert(attached.IsAttachedTo("desktop") && attached.IsAttachedTo("desktop1"), "Shared attached storage did not match both parents.");
Assert(!attached.IsAttachedTo("desk") && !attached.IsAttachedTo("Desktop"), "Storage matched the wrong desktop.");
Assert(!(attached with { Orphaned = true }).IsAttachedTo("desktop"), "Orphan storage appeared attached.");
Console.WriteLine("PASS: exact attached-storage parent association and orphan exclusion");

static async Task ExpectSetupFailure(RequirementsCoordinator coordinator, bool install, string message)
{
    bool failed = false;
    try { await coordinator.EnsureAsync(install, _ => { }); }
    catch (InvalidOperationException error) { failed = error.Message.Contains(message, StringComparison.OrdinalIgnoreCase); }
    Assert(failed, "Pre-flight did not report the expected failure: " + message);
}
var healthy = new RequirementSnapshot(true, true, true, "3.0.1", true, true, true);
var platform = new FakeRequirements([healthy], new(true, false));
await new RequirementsCoordinator(platform).EnsureAsync(true, _ => { });
Assert(platform.InstallCalls == 0, "Healthy machine was modified.");
platform = new([healthy with { WslVersion = null, WslcAvailable = false, VirtualMachinePlatformEnabled = false }, healthy], new(true, false));
await new RequirementsCoordinator(platform).EnsureAsync(true, _ => { });
Assert(platform.InstallCalls == 1 && platform.InstallWsl, "Missing WSL did not trigger installation and recheck.");
platform = new([healthy with { WslVersion = "2.9.3" }, healthy], new(true, false));
await new RequirementsCoordinator(platform).EnsureAsync(true, _ => { });
Assert(platform.InstallCalls == 1 && !platform.InstallWsl, "Outdated WSL did not trigger update.");
platform = new([healthy with { WslcAvailable = false }, healthy], new(true, false));
await new RequirementsCoordinator(platform).EnsureAsync(true, _ => { });
Assert(platform.InstallCalls == 1 && !platform.InstallWsl, "Missing WSLC did not trigger update.");
platform = new([healthy with { FirmwareVirtualizationAvailable = false }], new(true, false));
await ExpectSetupFailure(new(platform), true, "firmware");
Assert(platform.InstallCalls == 0, "Installer ran for a firmware-only problem.");
platform = new([healthy with { WindowsSupported = false }], new(true, false));
await ExpectSetupFailure(new(platform), true, "Update Windows");
Assert(platform.InstallCalls == 0, "Installer ran on unsupported Windows.");
platform = new([healthy with { RestartPending = true }], new(true, false));
await ExpectSetupFailure(new(platform), true, "Restart Windows");
Assert(platform.InstallCalls == 0, "Installer repeated while restart was pending.");
platform = new([healthy with { WslVersion = null }], new(true, true));
await ExpectSetupFailure(new(platform), true, "Restart Windows");
Assert(platform.InstallCalls == 1 && platform.ProbeCalls == 1, "Restart-required setup accessed runtime before reboot.");
platform = new([healthy with { WslVersion = null }], new(false, false, "administrator consent was declined"));
await ExpectSetupFailure(new(platform), true, "consent was declined");
platform = new([healthy with { WslVersion = null }], new(true, false));
await ExpectSetupFailure(new(platform), false, "Check requirements");
Assert(platform.InstallCalls == 0, "Setup retried without an explicit retry.");
platform = new([healthy with { WslVersion = null }, healthy with { WslVersion = "2.0.0" }], new(true, false));
await ExpectSetupFailure(new(platform), true, "not ready");
Assert(platform.InstallCalls == 1, "Installer entered a retry loop after failed verification.");
Console.WriteLine("PASS: pre-flight healthy/missing/outdated/WSLC states, one-shot auto setup, post-install verification, firmware/OS blocks, restart handling and declined elevation");

sealed class FakeRequirements(IReadOnlyList<RequirementSnapshot> snapshots, RequirementInstallResult result) : IRequirementsPlatform
{
    public int ProbeCalls { get; private set; }
    public int InstallCalls { get; private set; }
    public bool InstallWsl { get; private set; }
    public Task<RequirementSnapshot> ProbeAsync() => Task.FromResult(snapshots[Math.Min(ProbeCalls++, snapshots.Count - 1)]);
    public Task<RequirementInstallResult> InstallAsync(bool installWsl, Action<string> report)
    {
        InstallCalls++; InstallWsl = installWsl; return Task.FromResult(result);
    }
}
