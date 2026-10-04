using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDWSLC.Core;

namespace PDWSLC.App;

public partial class MainWindow : Window
{
    private sealed record Node(string FolderId, DesktopInfo? Desktop = null, StorageInfo? Storage = null)
    {
        public bool IsFolder => Desktop is null && Storage is null;
        public bool CanMove => Desktop is not null || Storage?.Orphaned == true;
    }
    private DesktopService? service;
    private DesktopService Service => service ??= new(BundledRuntime.GetBridgePath());
    private readonly RequirementsCoordinator requirements = new(new WindowsRequirementsPlatform());
    private bool preflightReady, setupAttempted;
    private FolderStore? folders;
    private IReadOnlyList<DesktopInfo> desktops = [];
    private IReadOnlyList<StorageInfo> storage = [];
    private string? loadedSession;
    private bool ready, busy;
    private Point dragStart;
    private Node? draggedNode;
    private string Session => SessionBox.Text.Trim();
    private bool CanAct => !busy && loadedSession == Session && folders is not null;

    public MainWindow() { InitializeComponent(); ready = true; BuildTree(); }

    private void AppendOutput(string line) => Dispatcher.InvokeAsync(() =>
    {
        if (OutputBox.Text.Length > 150_000) OutputBox.Text = OutputBox.Text[^100_000..];
        OutputBox.AppendText(line + Environment.NewLine); OutputBox.ScrollToEnd();
    });
    private async Task OperateAsync(string label, Func<Task> operation)
    {
        if (busy) return;
        busy = true; StatusText.Text = label; StatusText.Foreground = Brushes.Black;
        Progress.Visibility = Visibility.Visible; DesktopTree.IsEnabled = false; SessionControls.IsEnabled = false;
        AppendOutput($"\n{DateTime.Now:T}  {label}");
        try { await operation(); StatusText.Text = "Completed. " + label; }
        catch (Exception error) { AppendOutput(error.Message); StatusText.Text = "Operation failed. See the output below."; StatusText.Foreground = Brushes.Firebrick; }
        finally { busy = false; Progress.Visibility = Visibility.Collapsed; DesktopTree.IsEnabled = true; SessionControls.IsEnabled = true; }
    }
    private async Task RefreshCoreAsync()
    {
        try
        {
            if (!preflightReady)
            {
                bool install = !setupAttempted;
                setupAttempted = true;
                await requirements.EnsureAsync(install, AppendOutput);
                preflightReady = true;
            }
            folders ??= new(Environment.GetEnvironmentVariable("PDWSLC_FOLDER_DATA_PATH") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDWSLC", "folders.json"));
            var list = await Service.RunAsync(new("List", Session: Session)); list.EnsureSuccess();
            var volumes = await Service.RunAsync(new("Storage", Session: Session)); volumes.EnsureSuccess();
            var newDesktops = DesktopService.ReadRows<DesktopInfo>(list.Output);
            var newStorage = DesktopService.ReadRows<StorageInfo>(volumes.Output);
            folders.Reconcile(Session, newDesktops, newStorage);
            desktops = newDesktops; storage = newStorage; loadedSession = Session;
            BuildTree(); AppendOutput($"Found {desktops.Count} desktops and {storage.Count(s => s.Orphaned)} orphaned home volumes.");
        }
        catch { desktops = []; storage = []; loadedSession = null; BuildTree(); throw; }
    }
    private async Task ExecuteAsync(DesktopRequest request, Action? succeeded = null)
    {
        var result = await Service.RunAsync(request, AppendOutput);
        if (result.ExitCode == 0) succeeded?.Invoke();
        try { await RefreshCoreAsync(); }
        catch (Exception error) { AppendOutput("Could not refresh current state: " + error.Message); }
        result.EnsureSuccess();
    }
    private void BuildTree()
    {
        if (!ready) return;
        var expanded = new HashSet<string>();
        void Remember(ItemCollection items)
        {
            foreach (TreeViewItem item in items) { if (item.Tag is Node { IsFolder: true } node && item.IsExpanded) expanded.Add(node.FolderId); Remember(item.Items); }
        }
        Remember(DesktopTree.Items);
        bool first = DesktopTree.Items.Count == 0;
        DesktopTree.Items.Clear();
        var layout = folders?.Get(Session) ?? new SessionFolders();
        TreeViewItem MakeFolder(DesktopFolder folder)
        {
            var item = Item("\uE8B7", folder.Name, new(folder.Id));
            item.IsExpanded = first || folder.Id == FolderStore.HostId || expanded.Contains(folder.Id);
            foreach (var child in layout.Folders.Where(f => f.ParentId == folder.Id).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) item.Items.Add(MakeFolder(child));
            foreach (var desktop in desktops.Where(d => layout.Desktops.GetValueOrDefault(d.Name, FolderStore.HostId) == folder.Id && (RunningOnly.IsChecked != true || d.State == "running")).OrderBy(d => d.Name))
            {
                var desktopItem = Item("\uE7F4", $"{desktop.Name}  ·  {desktop.State}", new(folder.Id, desktop));
                foreach (var volume in storage.Where(s => s.IsAttachedTo(desktop.Name)).OrderBy(s => s.Name))
                    desktopItem.Items.Add(Item("\uEDA2", $"{volume.Name}  ·  attached storage", new(folder.Id, Storage: volume)));
                desktopItem.IsExpanded = true;
                item.Items.Add(desktopItem);
            }
            // Orphans remain visible even with the running-desktops filter enabled.
            foreach (var volume in storage.Where(s => s.Orphaned && layout.Storage.GetValueOrDefault(s.Name, FolderStore.HostId) == folder.Id).OrderBy(s => s.Name))
                item.Items.Add(Item("\uEDA2", $"{volume.Name}  ·  orphaned storage", new(folder.Id, Storage: volume)));
            return item;
        }
        DesktopTree.Items.Add(MakeFolder(layout.Folders.Single(f => f.Id == FolderStore.HostId)));
    }
    private static TreeViewItem Item(string glyph, string label, Node node)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = glyph, FontFamily = new("Segoe MDL2 Assets"), Foreground = node.IsFolder ? Brushes.DarkGoldenrod : Brushes.SteelBlue, Margin = new(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = label });
        var item = new TreeViewItem { Header = header, Tag = node };
        System.Windows.Automation.AutomationProperties.SetName(item, label);
        return item;
    }
    private static TreeViewItem? TreeItem(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is TreeViewItem item) return item;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
    private void Tree_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = TreeItem(e.OriginalSource);
        if (item is not null) { item.IsSelected = true; item.Focus(); }
        else if (DesktopTree.Items.Count > 0) ((TreeViewItem)DesktopTree.Items[0]).IsSelected = true;
    }
    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (busy) { e.Handled = true; return; }
        var node = (DesktopTree.SelectedItem as TreeViewItem)?.Tag as Node ?? new(FolderStore.HostId);
        var menu = DesktopTree.ContextMenu; menu.Items.Clear();
        MenuItem Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        if (node.IsFolder)
        {
            Add("New desktop…", () => CreateDesktop(node.FolderId), CanAct);
            Add("New folder…", () => EditFolder(node.FolderId, false), CanAct);
            if (node.FolderId != FolderStore.HostId)
            {
                menu.Items.Add(new Separator());
                Add("Rename folder…", () => EditFolder(node.FolderId, true), CanAct);
                Add("Delete folder", () => ChangeFolders(() => folders!.DeleteFolder(Session, node.FolderId)), CanAct);
            }
            menu.Items.Add(new Separator());
            Add("Refresh", async () => await OperateAsync("Refreshing desktops…", RefreshCoreAsync));
            Add("Check requirements", async () =>
            {
                preflightReady = false; setupAttempted = false; loadedSession = null;
                await OperateAsync("Checking requirements…", RefreshCoreAsync);
            });
        }
        else if (node.Storage is { Orphaned: false } attached)
        {
            Add("Properties", () =>
            {
                var window = new EditorWindow(this, attached.Name + " Properties");
                window.Text($"Storage: {attached.Name}\nState: Attached\nUsed by: {attached.Containers}\nManagement: {attached.Management}");
                window.Show();
            }, CanAct);
        }
        else
        {
            if (node.Desktop is { } desktop)
            {
                Add("Open", async () => await RuntimeAction("Open", desktop), CanAct).FontWeight = FontWeights.Bold;
                Add("Start", async () => await RuntimeAction("Start", desktop), CanAct && desktop.State != "running");
                Add("Stop", async () => await RuntimeAction("Stop", desktop), CanAct && desktop.State == "running");
            }
            var move = Add("Move to folder", () => { }, CanAct);
            var layout = folders!.Get(Session);
            foreach (var folder in layout.Folders.OrderBy(f => FolderStore.FolderPath(layout, f.Id)))
            {
                var target = new MenuItem { Header = FolderStore.FolderPath(layout, folder.Id), IsEnabled = folder.Id != node.FolderId };
                target.Click += (_, _) => Move(node, folder.Id); move.Items.Add(target);
            }
            Add(node.Desktop is null ? "Delete storage…" : "Delete…", () => Delete(node), CanAct);
            if (node.Desktop is { } details)
            {
                menu.Items.Add(new Separator());
                Add("Properties", async () => await ShowProperties(details, node.FolderId), CanAct);
            }
        }
    }
    private void ChangeFolders(Action action)
    {
        if (!CanAct) return;
        try { action(); BuildTree(); StatusText.Text = "Folder organization saved."; }
        catch (Exception error) { AppendOutput(error.Message); StatusText.Text = error.Message; }
    }
    private void Move(Node node, string target) => ChangeFolders(() =>
    {
        if (node.Desktop is { } desktop) folders!.MoveDesktop(Session, desktop, target);
        else if (node.Storage is { } volume) folders!.MoveStorage(Session, volume, target);
    });
    private void EditFolder(string id, bool rename)
    {
        var layout = folders!.Get(Session);
        var dialog = new EditorWindow(this, rename ? "Rename folder" : "New folder");
        var name = dialog.Field("Folder name", rename ? layout.Folders.Single(f => f.Id == id).Name : "New folder", "FolderName");
        dialog.Submit(rename ? "Rename" : "Create folder", () =>
        {
            if (rename) folders.RenameFolder(Session, id, name.Text); else folders.AddFolder(Session, id, name.Text);
            BuildTree(); return true;
        });
        dialog.ShowDialog();
    }
    private async void CreateDesktop(string folder)
    {
        var dialog = new EditorWindow(this, "New desktop");
        dialog.Text("Folder: " + FolderStore.FolderPath(folders!.Get(Session), folder));
        var name = dialog.Field("Name", "desktop", "NameBox");
        var image = dialog.Field("Image (KDE validated previously; Xfce/custom images need validation)", "lscr.io/linuxserver/webtop:ubuntu-kde", "ImageBox");
        var port = dialog.Field("HTTPS port (use a separate port for each desktop)", "3001", "PortBox");
        var zone = dialog.Field("Timezone", "Asia/Jerusalem", "TimeZoneBox");
        DesktopRequest? request = null;
        dialog.Submit("Create and start", () =>
        {
            if (!int.TryParse(port.Text, out int number)) throw new ArgumentException("Enter a numeric HTTPS port.");
            if (string.IsNullOrWhiteSpace(image.Text)) throw new ArgumentException("Enter an image.");
            if (desktops.Any(d => d.Name == name.Text.Trim())) throw new ArgumentException("A desktop with this name already exists. Use its Start action.");
            request = new("Start", name.Text.Trim(), Session, image.Text.Trim(), number, zone.Text.Trim());
            DesktopService.BuildArguments("unused", request);
            // Save placement before runtime creation so partial creation is discoverable here.
            folders.MoveDesktop(Session, new(request.Name!, "", request.Image!, null, request.Name + "-home", "Managed"), folder);
            return true;
        });
        if (dialog.ShowDialog() == true && request is not null)
            await OperateAsync("Creating " + request.Name + "… The first download may take several minutes.", () => ExecuteAsync(request));
    }
    private async void Delete(Node node)
    {
        string? name = node.Desktop?.Name ?? node.Storage?.Desktop;
        if (string.IsNullOrWhiteSpace(name) && node.Storage?.Name.EndsWith("-home", StringComparison.Ordinal) == true) name = node.Storage.Name[..^5];
        if (string.IsNullOrWhiteSpace(name) || (node.Storage is not null && node.Storage.Name != name + "-home")) { StatusText.Text = "Storage does not have an expected desktop home name; it was retained."; return; }
        var dialog = new EditorWindow(this, node.Desktop is null ? "Delete orphaned storage" : "Delete desktop");
        dialog.Text("Target: " + (node.Desktop?.Name ?? node.Storage!.Name));
        dialog.Text("Container deletion removes software installed in its writable layer. Home deletion permanently removes files and settings.");
        var removeStorage = dialog.Check("Permanently delete home files and settings", node.Desktop is null);
        removeStorage.IsEnabled = node.Desktop is not null;
        var adopt = dialog.Check("Authorize deletion of this unlabelled legacy home volume");
        adopt.IsEnabled = removeStorage.IsChecked == true;
        removeStorage.Checked += (_, _) => adopt.IsEnabled = true;
        removeStorage.Unchecked += (_, _) => { adopt.IsChecked = false; adopt.IsEnabled = false; };
        DesktopRequest? request = null;
        dialog.Submit("Delete selected target", () => { request = new("Delete", name, Session, IncludeStorage: removeStorage.IsChecked == true, Adopt: adopt.IsChecked == true); return true; });
        if (dialog.ShowDialog() == true && request is not null)
            await OperateAsync("Deleting " + name + "…", () => ExecuteAsync(request, () => folders!.ForgetDeleted(Session, name, request.IncludeStorage)));
    }
    private async Task RuntimeAction(string action, DesktopInfo desktop) =>
        await OperateAsync($"{action}: {desktop.Name}", () => ExecuteAsync(new(action, desktop.Name, Session)));
    private async Task ShowProperties(DesktopInfo desktop, string folder)
    {
        string path = FolderStore.FolderPath(folders!.Get(Session), folder);
        var window = new PropertiesWindow(this, desktop, path); window.Show();
        await OperateAsync("Loading properties: " + desktop.Name, async () =>
        {
            try
            {
                var result = await Service.RunAsync(new("Status", desktop.Name, Session)); result.EnsureSuccess();
                var current = JsonSerializer.Deserialize<DesktopInfo>(result.Output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing desktop properties.");
                window.Update(current, path);
                var logs = await Service.RunAsync(new("Logs", desktop.Name, Session)); logs.EnsureSuccess();
                window.SetLogs(string.IsNullOrWhiteSpace(logs.Output) ? "No logs available." : logs.Output);
            }
            catch (Exception error) { window.SetLogs(error.Message); throw; }
        });
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await OperateAsync("Loading desktops…", RefreshCoreAsync);
    private void Filter_Changed(object sender, RoutedEventArgs e) => BuildTree();
    private void Session_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        loadedSession = null; desktops = []; storage = []; BuildTree();
        StatusText.Text = "Session changed. Right-click Host and choose Refresh.";
    }
    private void Tree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) { }
    private async void Tree_LeftDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(DesktopTree); draggedNode = TreeItem(e.OriginalSource)?.Tag as Node;
        if (CanAct && e.ClickCount == 2 && draggedNode?.Desktop is { } desktop)
        {
            draggedNode = null;
            e.Handled = true;
            await RuntimeAction("Open", desktop);
        }
    }
    private async void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (CanAct && e.Key == Key.Enter && (DesktopTree.SelectedItem as TreeViewItem)?.Tag is Node { Desktop: { } desktop })
        {
            e.Handled = true;
            await RuntimeAction("Open", desktop);
        }
    }
    private void Tree_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CanAct || e.LeftButton != MouseButtonState.Pressed || draggedNode?.CanMove != true) return;
        var position = e.GetPosition(DesktopTree);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = draggedNode; draggedNode = null;
        DragDrop.DoDragDrop(DesktopTree, new DataObject("PDWSLC.Node", node), DragDropEffects.Move);
    }
    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var target = TreeItem(e.OriginalSource)?.Tag as Node;
        e.Effects = CanAct && target?.IsFolder == true && e.Data.GetData("PDWSLC.Node") is Node { CanMove: true } ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Tree_Drop(object sender, DragEventArgs e)
    {
        if (CanAct && TreeItem(e.OriginalSource)?.Tag is Node { IsFolder: true } target && e.Data.GetData("PDWSLC.Node") is Node { CanMove: true } node) Move(node, target.FolderId);
        e.Handled = true;
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    { if (busy) { e.Cancel = true; StatusText.Text = "Wait for the current operation to finish before closing."; } }
}
