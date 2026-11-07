using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MeshCentralClient.Core;
using Microsoft.Win32;

namespace MeshCentralClient.App;

/// <summary>One row of the remote folder. / Una fila de la carpeta remota.</summary>
public sealed class FileRow(RemoteEntry entry)
{
    public RemoteEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Kind => Entry.Kind switch { RemoteEntryKind.Drive => "💽", RemoteEntryKind.Folder => "📁", _ => "📄" };
    public string Size => Entry.Kind == RemoteEntryKind.Folder ? "" : FilesWindow.Human(Entry.Size);
    public string Modified => Entry.Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public bool IsContainer => Entry.Kind != RemoteEntryKind.File;
    public override string ToString() => Name;
}

/// <summary>
/// The files of one device: browse, create folders, rename, delete, upload (also by dropping files) and download.
/// Every change is checked with a new listing, because the agent does not answer mkdir, rename or rm.
/// Los ficheros de un equipo: explorar, crear carpetas, renombrar, borrar, subir (también soltando ficheros) y bajar.
/// Cada cambio se comprueba volviendo a listar, porque el agente no contesta a mkdir, rename ni rm.
/// </summary>
public sealed class FilesWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly DeviceRow _device;
    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly TextBox _pathBox = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Width = 160, Height = 4, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
    private readonly List<Button> _actions = new();
    private FilesSession? _files;
    private CancellationTokenSource? _transfer;
    private string _path = "";
    private bool _closing;

    public FilesWindow(MeshServerSession session, DeviceRow device)
    {
        _session = session;
        _device = device;
        Title = $"{L.T("Files")} — {device.Name}";
        Width = 900; Height = 600; MinWidth = 520; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Id(this, "FilesWindow"); Id(_pathBox, "PathBox"); Id(_list, "FileList"); Id(_status, "FilesStatus"); Id(_cancel, "TransferCancel");
        _cancel.Content = L.T("Cancel");
        _cancel.Click += (_, _) => _transfer?.Cancel();

        var bar = new DockPanel { Margin = new Thickness(12, 10, 12, 6) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Action("UpButton", "⬆ " + L.T("Up"), async () => await GoUpAsync()));
        left.Children.Add(Action("RefreshFiles", L.T("Refresh"), async () => await ShowAsync(_path)));
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Action("NewFolderButton", L.T("NewFolder"), async () => await NewFolderAsync()));
        right.Children.Add(Action("UploadButton", L.T("Upload"), async () => await PickAndUploadAsync()));
        right.Children.Add(Action("DownloadButton", L.T("Download"), async () => await DownloadSelectedAsync()));
        right.Children.Add(Action("RenameButton", L.T("Rename"), async () => await RenameAsync()));
        right.Children.Add(Action("DeleteButton", L.T("Delete"), async () => await DeleteAsync()));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        _pathBox.Margin = new Thickness(8, 0, 8, 0);
        _pathBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ShowAsync(FromDisplay(_pathBox.Text)); };
        bar.Children.Add(_pathBox);

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Width = 36, DisplayMemberBinding = new Binding(nameof(FileRow.Kind)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColName"), Width = 380, DisplayMemberBinding = new Binding(nameof(FileRow.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColSize"), Width = 110, DisplayMemberBinding = new Binding(nameof(FileRow.Size)) });
        grid.Columns.Add(new GridViewColumn { Header = L.T("ColModified"), Width = 170, DisplayMemberBinding = new Binding(nameof(FileRow.Modified)) });
        _list.View = grid;
        _list.ItemsSource = _rows;
        _list.Margin = new Thickness(12, 0, 12, 0);
        _list.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await OpenSelectedAsync();
            else if (e.Key == Key.Back) await GoUpAsync();
            else if (e.Key == Key.Delete) await DeleteAsync();
            else if (e.Key == Key.F2) await RenameAsync();
            else if (e.Key == Key.F5) await ShowAsync(_path);
        };
        _list.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _path.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _list.Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files.Where(File.Exists).ToArray()); };

        var foot = new DockPanel { Margin = new Thickness(12, 6, 12, 10) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Children = { _progress, _cancel } };
        DockPanel.SetDock(tools, Dock.Right);
        foot.Children.Add(tools);
        foot.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(foot);
        root.Children.Add(_list);
        Content = root;

        Loaded += async (_, _) => await StartAsync();
        Closed += async (_, _) => { _closing = true; _transfer?.Cancel(); if (_files is not null) await _files.DisposeAsync(); };
    }

    private static void Id(UIElement e, string id) => System.Windows.Automation.AutomationProperties.SetAutomationId(e, id);

    private Button Action(string id, string text, Func<Task> run)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        Id(b, id);
        b.Click += async (_, _) => await run();
        _actions.Add(b);
        return b;
    }

    private void Busy(bool busy)
    {
        foreach (var b in _actions) b.IsEnabled = !busy;
        _pathBox.IsEnabled = !busy;
        _list.IsEnabled = !busy;
    }

    private async Task StartAsync()
    {
        _status.Text = L.T("Connecting");
        Busy(true);
        try
        {
            _files = await FilesSession.OpenAsync(_session, _device.Id);
            _files.Tunnel.Closed += (_, e) => Dispatcher.BeginInvoke(() => { if (!_closing) Fail(e); });
        }
        catch (MeshClientException ex) { Fail(ex); return; }
        await ShowAsync(_device.IsWindows ? "" : "/");
    }

    // ------------------------------------------------------------------ paths / rutas

    internal static string Join(string folder, string name) =>
        folder.Length == 0 ? name.TrimEnd('/', '\\') + "/" : folder.TrimEnd('/') + "/" + name;

    internal static string ParentOf(string path)
    {
        var p = path.TrimEnd('/');
        if (p.Length == 0) return "/";
        var i = p.LastIndexOf('/');
        if (i < 0) return "";                         // "C:" -> drives / unidades
        if (i == 0) return "/";
        if (i == 2 && p[1] == ':') return p[..3];     // "C:/Users" -> "C:/"
        return p[..i];
    }

    private string Display(string path) => path.Length == 0 ? L.T("Drives") : _device.IsWindows ? path.Replace('/', '\\') : path;

    private static string FromDisplay(string text) => text.Trim().Replace('\\', '/');

    internal static string Human(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + u[i];
    }

    // ------------------------------------------------------------------ browsing / explorar

    private async Task<bool> ShowAsync(string path, string? select = null)
    {
        if (_files is null) return false;
        Busy(true);
        try
        {
            var folder = await _files.ListAsync(path);
            _path = path;
            _pathBox.Text = Display(path);
            _rows.Clear();
            foreach (var e in folder.Entries.OrderBy(e => e.Kind == RemoteEntryKind.File).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                _rows.Add(new FileRow(e));
            _status.Text = path.Length == 0 ? "" : L.F("ItemsCount", _rows.Count(r => r.IsContainer), _rows.Count(r => !r.IsContainer)) + "   " + L.T("DropHint");
            if (select is not null) _list.SelectedItem = _rows.FirstOrDefault(r => r.Name == select);
            return true;
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); _pathBox.Text = Display(_path); return false; }
        finally { Busy(false); _list.Focus(); }
    }

    private async Task GoUpAsync() { if (_path.Length > 0) await ShowAsync(ParentOf(_path)); }

    private async Task OpenSelectedAsync()
    {
        if (_list.SelectedItem is not FileRow row) return;
        if (row.IsContainer) await ShowAsync(Join(_path, row.Name));
        else await DownloadSelectedAsync();
    }

    /// <summary>The agent does not answer changes: list again until it shows, or report it. / El agente no contesta a los cambios: se vuelve a listar hasta que se vea, o se avisa.</summary>
    private async Task Confirm(Func<RemoteFolder, bool> done, string what, string? select = null)
    {
        for (var i = 0; i < 15; i++)
        {
            var f = await _files!.ListAsync(_path);
            if (done(f)) { await ShowAsync(_path, select); return; }
            await Task.Delay(200);
        }
        await ShowAsync(_path);
        ErrorWindow.Show(this, UserError.From(new MeshClientException(MeshFailure.Rejected, "ficheros/gestionar-ficheros/rechazo-remoto", $"The device did not {what}.")));
    }

    private async Task NewFolderAsync()
    {
        if (_files is null || _path.Length == 0) return;
        if (InputDialog.Ask(this, L.T("NewFolder"), L.T("FolderName")) is not { } name) return;
        await Guard(async () =>
        {
            await _files.CreateFolderAsync(Join(_path, name));
            await Confirm(f => f.Entries.Any(e => e.Name == name && e.Kind == RemoteEntryKind.Folder), $"create the folder \"{name}\"", name);
        });
    }

    private async Task RenameAsync()
    {
        if (_files is null || _path.Length == 0 || _list.SelectedItem is not FileRow row) return;
        if (InputDialog.Ask(this, L.T("Rename"), L.T("NewName"), row.Name) is not { } name || name == row.Name) return;
        await Guard(async () =>
        {
            await _files.RenameAsync(_path, row.Name, name);
            await Confirm(f => f.Entries.Any(e => e.Name == name) && f.Entries.All(e => e.Name != row.Name), $"rename \"{row.Name}\"", name);
        });
    }

    private async Task DeleteAsync()
    {
        if (_files is null || _path.Length == 0) return;
        var names = _list.SelectedItems.OfType<FileRow>().Select(r => r.Name).ToList();
        if (names.Count == 0) return;
        if (MessageBox.Show(this, L.F("ConfirmDelete", names.Count, Display(_path)), L.T("Delete"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Guard(async () =>
        {
            await _files.DeleteAsync(_path, names, recursive: true);
            await Confirm(f => f.Entries.All(e => !names.Contains(e.Name)), "delete everything selected");
        });
    }

    private async Task Guard(Func<Task> work)
    {
        Busy(true);
        try { await work(); }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally { Busy(false); }
    }

    // ------------------------------------------------------------------ transfers / transferencias

    private async Task PickAndUploadAsync()
    {
        if (_path.Length == 0) return;
        var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("Upload") };
        if (dlg.ShowDialog(this) == true) await UploadAsync(dlg.FileNames);
    }

    private async Task UploadAsync(string[] localFiles)
    {
        if (_files is null || _path.Length == 0 || localFiles.Length == 0) return;
        var target = _path;
        await Transfer(async ct =>
        {
            foreach (var local in localFiles)
            {
                var name = Path.GetFileName(local);
                if (_rows.Any(r => r.Name == name) &&
                    MessageBox.Show(this, L.F("ConfirmOverwrite", name), L.T("Upload"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    continue;
                FileStream src;
                try { src = File.OpenRead(local); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/subir/permisos-fichero", $"\"{local}\" could not be read.", ex);
                }
                await using (src)
                {
                    var total = src.Length;
                    _progress.Value = 0;
                    await _files.UploadAsync(target, name, src, new Progress<long>(n => Report("Uploading", name, n, total)), ct);
                }
                _status.Text = L.F("Done", name);
            }
            await ShowAsync(target, localFiles.Length == 1 ? Path.GetFileName(localFiles[0]) : null);
        });
    }

    private async Task DownloadSelectedAsync()
    {
        if (_files is null) return;
        var rows = _list.SelectedItems.OfType<FileRow>().Where(r => !r.IsContainer).ToList();
        if (rows.Count == 0) return;
        List<(FileRow Row, string Local)> plan;
        if (rows.Count == 1)
        {
            var dlg = new SaveFileDialog { FileName = rows[0].Name, Title = L.T("Download") };
            if (dlg.ShowDialog(this) != true) return;
            plan = new() { (rows[0], dlg.FileName) };
        }
        else
        {
            var dlg = new OpenFolderDialog { Title = L.T("Download") };
            if (dlg.ShowDialog(this) != true) return;
            plan = rows.Select(r => (r, Path.Combine(dlg.FolderName, r.Name))).ToList();
        }
        var folder = _path;
        await Transfer(async ct =>
        {
            foreach (var (row, local) in plan)
            {
                // Written aside and moved at the end: a cut download never looks like a whole file.
                // Se escribe aparte y se mueve al final: una descarga cortada nunca parece un fichero entero.
                var part = local + ".part";
                try
                {
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                        await _files.DownloadAsync(Join(folder, row.Name), dst, new Progress<long>(n => Report("Downloading", row.Name, n, row.Entry.Size)), ct);
                    File.Move(part, local, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(part);
                    throw new MeshClientException(MeshFailure.Rejected, "ficheros/descargar/permisos-fichero", $"\"{local}\" could not be written.", ex);
                }
                catch { TryDelete(part); throw; }
                _status.Text = L.F("Done", local);
            }
        });
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    private void Report(string what, string name, long done, long total)
    {
        _progress.Value = total > 0 ? (double)done / total : 0;
        _status.Text = L.F(what, name, total > 0 ? $"{Human(done)} / {Human(total)}" : Human(done));
    }

    private async Task Transfer(Func<CancellationToken, Task> work)
    {
        _transfer = new CancellationTokenSource();
        Busy(true);
        _progress.Maximum = 1;
        _progress.Visibility = _cancel.Visibility = Visibility.Visible;
        try { await work(_transfer.Token); }
        catch (OperationCanceledException) { _status.Text = L.T("Cancel"); }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        finally
        {
            _progress.Visibility = _cancel.Visibility = Visibility.Collapsed;
            Busy(false);
            _transfer.Dispose();
            _transfer = null;
        }
    }

    private void Fail(MeshClientException ex)
    {
        Busy(true);
        var err = UserError.From(ex, _session.ServerInfo.Version);
        _status.Text = $"{err.Message}  {err.Footer}";
    }
}
