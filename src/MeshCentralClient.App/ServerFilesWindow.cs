using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using Microsoft.Win32;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One entry of a server folder as a row. / Una entrada de una carpeta del servidor como fila.</summary>
public sealed class ServerFileRow(ServerFileEntry e)
{
    public ServerFileEntry Entry => e;
    public string Name => e.IsFolder ? "📁 " + e.Name : e.Name;
    public string Size => e.IsFolder ? "" : Sizes.Short(e.Size);
    public string Modified => e.Modified?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    public override string ToString() => e.Name;
}

/// <summary>
/// The files kept on the server ("My Files", and each device group's when the account may), as the website's: browse,
/// make folders, rename, delete, upload and download. The server only sends an app files below 200 KB; bigger ones
/// open in its website. / Los ficheros guardados en el servidor («Mis ficheros» y los de cada grupo si la cuenta puede),
/// como en la web. El servidor solo da a una app ficheros de menos de 200 KB; los mayores se abren en su web.
/// </summary>
public sealed class ServerFilesWindow : Window
{
    private readonly MeshServerSession _session;
    private IReadOnlyList<ServerFileRoot> _roots = [];
    private readonly List<string> _folder = new();
    private bool _filling;
    private readonly ComboBox _root = new() { MinWidth = 200 };
    private readonly TextBlock _path = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListView _list = new() { Margin = new Thickness(16, 0, 16, 8) };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 0, 16, 12), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _up = new() { Content = L.T("SfUp"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _newFolder = new() { Content = L.T("SfNewFolder"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _upload = new() { Content = L.T("Upload"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _download = new() { Content = L.T("Download"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _rename = new() { Content = L.T("SfRename"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _delete = new() { Content = L.T("SfDelete"), MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _refresh = new() { Content = L.T("Refresh"), MinWidth = 90 };

    public ServerFilesWindow(MeshServerSession session)
    {
        _session = session;
        Title = L.T("ServerFilesItem");
        Width = 900; Height = 600; MinWidth = 600; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (this, "ServerFilesWindow"), (_root, "SfRoot"), (_path, "SfPath"), (_list, "SfList"), (_status, "SfStatus"), (_up, "SfUp"),
            (_newFolder, "SfNewFolder"), (_upload, "SfUpload"), (_download, "SfDownload"), (_rename, "SfRename"), (_delete, "SfDelete"), (_refresh, "SfRefresh"),
        }) AP.SetAutomationId(e, id);
        AP.SetName(_root, L.T("SfRootLabel"));

        var grid = new GridView();
        void Col(string head, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(head), Width = width, DisplayMemberBinding = new Binding(path) });
        Col("SfName", nameof(ServerFileRow.Name), 420);
        Col("SfSize", nameof(ServerFileRow.Size), 110);
        Col("SfModified", nameof(ServerFileRow.Modified), 170);
        _list.View = grid;

        var where = new DockPanel { Margin = new Thickness(16, 12, 16, 6) };
        where.Children.Add(_root);
        where.Children.Add(_path);
        var bar = new WrapPanel { Margin = new Thickness(16, 0, 16, 8) };
        foreach (var b in new[] { _up, _newFolder, _upload, _download, _rename, _delete, _refresh }) bar.Children.Add(b);
        var top = new StackPanel { Children = { where, bar } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_list);
        Content = root;

        _root.SelectionChanged += (_, _) => { if (_filling) return; _folder.Clear(); ShowFolder(); };
        _list.SelectionChanged += (_, _) => Enable();
        _list.MouseDoubleClick += async (_, _) =>
        {
            if (_list.SelectedItem is not ServerFileRow r) return;
            if (r.Entry.IsFolder) { _folder.Add(r.Entry.Name); ShowFolder(); }
            else await DownloadAsync();
        };
        _up.Click += (_, _) => { if (_folder.Count > 0) { _folder.RemoveAt(_folder.Count - 1); ShowFolder(); } };
        _newFolder.Click += async (_, _) => await NewFolderAsync();
        _upload.Click += async (_, _) => await UploadAsync();
        _download.Click += async (_, _) => await DownloadAsync();
        _rename.Click += async (_, _) => await RenameAsync();
        _delete.Click += async (_, _) => await DeleteAsync();
        _refresh.Click += async (_, _) => await Run(async () => { Set(await _session.GetServerFilesAsync()); return null; });
        Loaded += async (_, _) => await Run(async () => { Set(await _session.GetServerFilesAsync()); return null; });
    }

    private ServerFileRoot? Current => _root.SelectedIndex is int i and >= 0 && i < _roots.Count ? _roots[i] : null;

    private void Set(IReadOnlyList<ServerFileRoot> roots)
    {
        var keep = Current?.Id;
        _roots = roots;
        _filling = true;
        _root.Items.Clear();
        foreach (var r in roots) _root.Items.Add(r.IsGroup ? L.F("SfGroupRoot", r.Name) : L.T("SfMyFiles"));
        var idx = roots.ToList().FindIndex(r => r.Id == keep);
        _root.SelectedIndex = idx >= 0 ? idx : roots.Count > 0 ? 0 : -1;
        _filling = false;
        // The folder may be gone (deleted elsewhere): go up to what still is. / La carpeta puede haberse ido: subir a lo que queda.
        while (_folder.Count > 0 && Current?.Find(_folder) is null) _folder.RemoveAt(_folder.Count - 1);
        ShowFolder();
    }


    private void ShowFolder()
    {
        var r = Current;
        _path.Text = "/" + string.Join("/", _folder);
        _list.ItemsSource = (r?.Find(_folder) ?? []).Select(e => new ServerFileRow(e)).ToList();
        _status.Text = r is null ? L.T("SfNone")
            : r.MaxBytes is long max ? L.F("SfQuota", Sizes.Short(r.UsedBytes), Sizes.Short(max)) : L.F("SfUsed", Sizes.Short(r.UsedBytes));
        Enable();
    }

    private void Enable()
    {
        var sel = _list.SelectedItems.OfType<ServerFileRow>().ToList();
        _up.IsEnabled = _folder.Count > 0;
        _newFolder.IsEnabled = _upload.IsEnabled = Current is not null;
        _download.IsEnabled = sel.Count == 1 && !sel[0].Entry.IsFolder;
        _rename.IsEnabled = sel.Count == 1;
        _delete.IsEnabled = sel.Count > 0;
    }

    private async Task Run(Func<Task<string?>> op)
    {
        IsEnabled = false;
        try { if (await op() is { } done) _status.Text = done; }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorWindow.Show(this, UserError.Unexpected(ex)); }
        finally { IsEnabled = true; Enable(); }
    }

    private Task NewFolderAsync() => Run(async () =>
    {
        if (Current is not { } r || InputDialog.Ask(this, L.T("SfNewFolder"), L.T("SfFolderName")) is not { Length: > 0 } name) return null;
        Set(await _session.CreateServerFolderAsync(r, _folder.ToList(), name));
        return L.F("SfFolderMade", name.Trim());
    });

    private Task RenameAsync() => Run(async () =>
    {
        if (Current is not { } r || _list.SelectedItem is not ServerFileRow row) return null;
        if (InputDialog.Ask(this, L.T("SfRename"), L.T("SfNewName"), row.Entry.Name) is not { Length: > 0 } name || name.Trim() == row.Entry.Name) return null;
        Set(await _session.RenameServerFileAsync(r, _folder.ToList(), row.Entry.Name, name));
        return L.F("SfRenamed", row.Entry.Name, name.Trim());
    });

    private Task DeleteAsync() => Run(async () =>
    {
        var names = _list.SelectedItems.OfType<ServerFileRow>().Select(x => x.Entry.Name).ToList();
        if (Current is not { } r || names.Count == 0) return null;
        if (MessageBox.Show(this, L.F("SfConfirmDelete", string.Join(", ", names)), L.T("SfDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
        Set(await _session.DeleteServerFilesAsync(r, _folder.ToList(), names));
        return L.F("SfDeleted", names.Count);
    });

    private Task UploadAsync() => Run(async () =>
    {
        if (Current is not { } r) return null;
        var dlg = new OpenFileDialog { Multiselect = true, Title = L.T("Upload") };
        if (dlg.ShowDialog(this) != true) return null;
        foreach (var path in dlg.FileNames)
        {
            var info = new FileInfo(path);
            if (info.Length > MeshServerSession.ServerFileUploadLimit) { OpenWeb(); return L.F("SfTooBigUp", info.Name); }
            var data = await File.ReadAllBytesAsync(path);
            _status.Text = L.F("SfUploading", info.Name);
            Set(await _session.UploadServerFileAsync(Current ?? r, _folder.ToList(), info.Name, data));
        }
        return L.F("SfUploaded", dlg.FileNames.Length);
    });

    private Task DownloadAsync() => Run(async () =>
    {
        if (Current is not { } r || _list.SelectedItem is not ServerFileRow { Entry: { IsFolder: false } file }) return null;
        if (file.Size >= MeshServerSession.ServerFileDownloadLimit)
        {
            if (MessageBox.Show(this, L.F("SfTooBigDown", file.Name), L.T("Download"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) OpenWeb();
            return null;
        }
        var dlg = new SaveFileDialog { FileName = file.Name, Title = L.T("Download") };
        if (dlg.ShowDialog(this) != true) return null;
        await File.WriteAllBytesAsync(dlg.FileName, await _session.DownloadServerFileAsync(r, _folder.ToList(), file));
        return L.F("SfDownloaded", file.Name);
    });

    /// <summary>The website's "My Files" (viewmode 5). / «Mis ficheros» de la web.</summary>
    private void OpenWeb()
    {
        try { Process.Start(new ProcessStartInfo(new Uri(_session.Server.BaseUri, "?viewmode=5").ToString()) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
