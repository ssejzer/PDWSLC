using System.Text.Json;

namespace PDWSLC.Core;

public sealed record DesktopFolder(string Id, string Name, string? ParentId);
public sealed class SessionFolders
{
    public List<DesktopFolder> Folders { get; set; } = [new(FolderStore.HostId, "Host", null)];
    public Dictionary<string, string> Desktops { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Storage { get; set; } = new(StringComparer.Ordinal);
}
public sealed class FolderDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SessionFolders> Sessions { get; set; } = new(StringComparer.Ordinal);
}

// Folder changes affect app metadata only. No runtime resource is renamed or moved.
public sealed class FolderStore
{
    public const string HostId = "host";
    private readonly string path;
    private FolderDocument document;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public FolderStore(string path)
    {
        this.path = path;
        document = File.Exists(path)
            ? JsonSerializer.Deserialize<FolderDocument>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Folder metadata is empty. The file was retained.")
            : new();
        if (document.Version != 1) throw new InvalidDataException("Unsupported folder metadata version. The file was retained.");
        foreach (var layout in document.Sessions.Values) Validate(layout);
    }

    public SessionFolders Get(string session) => Clone(document.Sessions.GetValueOrDefault(session) ?? new());
    public string DesktopLocation(string session, string name) => Get(session).Desktops.GetValueOrDefault(name, HostId);
    public string StorageLocation(string session, string name) => Get(session).Storage.GetValueOrDefault(name, HostId);

    public string AddFolder(string session, string parent, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        Change(session, layout =>
        {
            RequireFolder(layout, parent);
            CheckName(layout, parent, name);
            layout.Folders.Add(new(id, name.Trim(), parent));
        });
        return id;
    }

    public void RenameFolder(string session, string id, string name) => Change(session, layout =>
    {
        if (id == HostId) throw new InvalidOperationException("Host cannot be renamed.");
        var folder = RequireFolder(layout, id);
        CheckName(layout, folder.ParentId!, name, id);
        layout.Folders[layout.Folders.IndexOf(folder)] = folder with { Name = name.Trim() };
    });

    public void DeleteFolder(string session, string id) => Change(session, layout =>
    {
        if (id == HostId) throw new InvalidOperationException("Host cannot be deleted.");
        var folder = RequireFolder(layout, id);
        var parent = folder.ParentId!;
        foreach (var child in layout.Folders.Where(f => f.ParentId == id).ToList())
        {
            CheckName(layout, parent, child.Name, id);
            layout.Folders[layout.Folders.IndexOf(child)] = child with { ParentId = parent };
        }
        foreach (var name in layout.Desktops.Where(pair => pair.Value == id).Select(pair => pair.Key).ToList()) layout.Desktops[name] = parent;
        foreach (var name in layout.Storage.Where(pair => pair.Value == id).Select(pair => pair.Key).ToList()) layout.Storage[name] = parent;
        layout.Folders.Remove(folder);
    });

    public void MoveDesktop(string session, DesktopInfo desktop, string folder) => Change(session, layout =>
    {
        RequireFolder(layout, folder);
        layout.Desktops[desktop.Name] = folder;
        foreach (var volume in HomeNames(desktop)) layout.Storage[volume] = folder;
    });

    public void MoveStorage(string session, StorageInfo storage, string folder) => Change(session, layout =>
    {
        RequireFolder(layout, folder);
        if (!storage.Orphaned) throw new InvalidOperationException("Only orphaned storage can be moved separately.");
        layout.Storage[storage.Name] = folder;
        var owner = !string.IsNullOrWhiteSpace(storage.Desktop) ? storage.Desktop :
            storage.Name.EndsWith("-home", StringComparison.Ordinal) ? storage.Name[..^5] : null;
        if (owner is not null) layout.Desktops[owner] = folder;
    });

    public void Reconcile(string session, IReadOnlyList<DesktopInfo> desktops, IReadOnlyList<StorageInfo> storage) => Change(session, layout =>
    {
        foreach (var desktop in desktops)
        {
            var homes = HomeNames(desktop).ToList();
            string location = layout.Desktops.GetValueOrDefault(desktop.Name)
                ?? homes.Select(h => layout.Storage.GetValueOrDefault(h)).FirstOrDefault(f => f is not null) ?? HostId;
            layout.Desktops[desktop.Name] = location;
            foreach (var home in homes) layout.Storage[home] = location;
        }
        foreach (var volume in storage)
        {
            if (layout.Storage.ContainsKey(volume.Name)) continue;
            string? owner = volume.Desktop;
            if (string.IsNullOrWhiteSpace(owner) && volume.Name.EndsWith("-home", StringComparison.Ordinal)) owner = volume.Name[..^5];
            layout.Storage[volume.Name] = owner is null ? HostId : layout.Desktops.GetValueOrDefault(owner, HostId);
        }
    });

    // Retain placement for resources missing temporarily or removed outside the app.
    public void ForgetDeleted(string session, string name, bool storageDeleted) => Change(session, layout =>
    {
        layout.Desktops.Remove(name);
        if (storageDeleted) layout.Storage.Remove(name + "-home");
    });

    public static string FolderPath(SessionFolders layout, string id)
    {
        var folder = RequireFolder(layout, id);
        return folder.ParentId is null ? folder.Name : FolderPath(layout, folder.ParentId) + " / " + folder.Name;
    }

    private static IEnumerable<string> HomeNames(DesktopInfo desktop) =>
        desktop.Storage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static DesktopFolder RequireFolder(SessionFolders layout, string id) =>
        layout.Folders.SingleOrDefault(f => f.Id == id) ?? throw new InvalidOperationException("Folder no longer exists.");
    private static void CheckName(SessionFolders layout, string parent, string name, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("Enter a folder name without slashes or line breaks.");
        if (layout.Folders.Any(f => f.ParentId == parent && f.Id != except && string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A folder with this name already exists here.");
    }
    private void Change(string session, Action<SessionFolders> change)
    {
        var next = Clone(document);
        if (!next.Sessions.TryGetValue(session, out var layout)) next.Sessions[session] = layout = new();
        change(layout);
        Validate(layout);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
            File.Move(temporary, path, overwrite: true);
            document = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(SessionFolders layout)
    {
        if (layout.Folders.Count(f => f.Id == HostId && f.Name == "Host" && f.ParentId is null) != 1 ||
            layout.Folders.Select(f => f.Id).Distinct().Count() != layout.Folders.Count)
            throw new InvalidDataException("Invalid folder tree. Metadata was retained.");
        foreach (var folder in layout.Folders)
        {
            var seen = new HashSet<string>();
            var current = folder;
            while (current.Id != HostId)
            {
                if (!seen.Add(current.Id) || current.ParentId is null) throw new InvalidDataException("Invalid folder ancestry.");
                current = RequireFolder(layout, current.ParentId);
            }
        }
        foreach (var target in layout.Desktops.Values.Concat(layout.Storage.Values)) RequireFolder(layout, target);
    }
}
