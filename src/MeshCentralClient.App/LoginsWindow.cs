using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One sign-in as a row. / Una entrada como fila.</summary>
public sealed class LoginRow(LoginEntry e)
{
    public string When => e.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Result => e.Kind switch
    {
        LoginKind.Valid => L.T("LoginOk"),
        LoginKind.Token => L.F("LoginToken", e.TokenName ?? ""),
        LoginKind.WrongSecondFactor => L.T("LoginBadCode"),
        LoginKind.Locked => L.T("LoginLocked"),
        LoginKind.WrongPassword => L.T("LoginBadPassword"),
        _ => L.T("LoginOther"),
    };
    public string Details => string.Join(" · ", e.Details);
    public bool Failed => e.Kind is LoginKind.WrongSecondFactor or LoginKind.Locked or LoginKind.WrongPassword;
    public override string ToString() => $"{When} {Result}";
}

/// <summary>
/// This account's previous sign-ins, good and failed, with where from, as the website's "Previous logins". MeshCentral
/// only notes sign-ins through its web pages. / Las entradas anteriores de la cuenta, buenas y fallidas, con desde dónde.
/// </summary>
public sealed class LoginsWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly ListView _list = new() { Margin = new Thickness(16, 0, 16, 16) };
    private readonly TextBlock _status = new() { Margin = new Thickness(16, 4, 16, 8), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    public LoginsWindow(MeshServerSession session)
    {
        _session = session;
        Title = L.T("LoginsTitle");
        Width = 860; Height = 520; MinWidth = 520; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AP.SetAutomationId(this, "LoginsWindow"); AP.SetAutomationId(_list, "LoginsList"); AP.SetAutomationId(_status, "LoginsStatus");
        var grid = new GridView();
        void Col(string head, string path, double width) => grid.Columns.Add(new GridViewColumn { Header = L.T(head), Width = width, DisplayMemberBinding = new Binding(path) });
        Col("LoginWhen", nameof(LoginRow.When), 150);
        Col("LoginResult", nameof(LoginRow.Result), 220);
        Col("LoginFrom", nameof(LoginRow.Details), 420);
        _list.View = grid;
        var help = new TextBlock { Text = L.T("LoginsHelp"), Margin = new Thickness(16, 12, 16, 0), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var top = new StackPanel { Children = { help, _status } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _status.Text = L.T("LoginsLoading");
        try
        {
            var rows = (await _session.GetPreviousLoginsAsync()).Select(e => new LoginRow(e)).ToList();
            _list.ItemsSource = rows;
            var failed = rows.Count(r => r.Failed);
            _status.Text = rows.Count == 0 ? L.T("LoginsNone") : L.F("LoginsCount", rows.Count, failed);
        }
        catch (MeshClientException ex)
        {
            var err = UserError.From(ex, _session.ServerInfo.Version);
            _status.Text = $"{err.Message}  {err.ActLine}  {err.Footer}";
        }
    }
}
