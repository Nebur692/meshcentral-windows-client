using System.Windows;
using System.Windows.Controls;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>
/// What a group asks of the person at the device and how it keeps its devices, laid out as the website's two dialogs.
/// What the server forces stays ticked and cannot be changed; the name sub-options only exist with name sync. The same
/// as the Android app's dialog.
/// Lo que un grupo pide a la persona del equipo y cómo guarda sus equipos, como los dos diálogos de la web. Lo que impone
/// el servidor sale marcado y no se puede cambiar; las opciones del nombre solo existen con la sincronización.
/// </summary>
public sealed class GroupSettingsWindow : Window
{
    private readonly ServerInfo _server;
    private readonly List<(CheckBox Box, int Bit)> _consent = new();
    private readonly CheckBox _record = new() { Content = L.T("FeatRecord") };
    private readonly CheckBox _sync;
    private readonly CheckBox _prefer = new() { Content = L.T("FeatPreferAgentName"), Margin = new Thickness(22, 4, 0, 0) };
    private readonly CheckBox _override = new() { Content = L.T("FeatAllowOverride"), Margin = new Thickness(22, 4, 0, 0) };
    private readonly CheckBox _onDisconnect = new() { Content = L.T("FeatRemoveOnDisconnect") };
    private readonly CheckBox _inactive = new() { Content = L.T("FeatRemoveInactive") };
    private readonly TextBox _days = new() { Width = 80, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBlock _daysError = new() { Text = L.F("FeatDaysRange", GroupSettings.MaxDays), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save = new() { Content = L.T("Save"), MinWidth = 100, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };

    public GroupSettings? Result { get; private set; }

    private GroupSettingsWindow(DeviceGroup g, ServerInfo server)
    {
        _server = server;
        _sync = new CheckBox { Content = L.T(g.Kind == DeviceGroupKind.IpKvm ? "FeatSyncPort" : "FeatSyncName") };
        Title = $"{L.T("GroupSettings")} — {g.Name}";
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxHeight = SystemParameters.WorkArea.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AP.SetAutomationId(this, "GroupSettingsWindow");
        foreach (var (e, id) in new (FrameworkElement, string)[]
        {
            (_record, "FeatRecord"), (_sync, "FeatSync"), (_prefer, "FeatPrefer"), (_override, "FeatOverride"), (_onDisconnect, "FeatOnDisconnect"),
            (_inactive, "FeatInactive"), (_days, "FeatDays"), (_save, "GroupSettingsSave"),
        }) AP.SetAutomationId(e, id);
        AP.SetName(_days, L.T("FeatDays"));

        var s = g.Settings;
        var consent = s.Consent | server.ForcedConsent;
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16), MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = L.T("ConsentHint"), Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        if (server.ForcedConsent != 0) panel.Children.Add(new TextBlock { Text = L.T("ConsentForced"), Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 4, 0, 0) });
        static TextBlock Section(string key) => new() { Text = L.T(key), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };
        void Consent(string label, GroupConsent bit, string id)
        {
            var forced = (server.ForcedConsent & (int)bit) != 0;
            var box = new CheckBox { Content = L.T(label), IsChecked = (consent & (int)bit) != 0, IsEnabled = !forced, Margin = new Thickness(0, 4, 0, 0) };
            AP.SetAutomationId(box, id);
            _consent.Add((box, (int)bit));
            panel.Children.Add(box);
        }
        panel.Children.Add(Section("ConsentDesktop"));
        Consent("ConsentNotify", GroupConsent.DesktopNotify, "ConsentDesktopNotify");
        Consent("ConsentPrompt", GroupConsent.DesktopPrompt, "ConsentDesktopPrompt");
        Consent("ConsentToolbar", GroupConsent.DesktopToolbar, "ConsentDesktopToolbar");
        panel.Children.Add(Section("ConsentTerminal"));
        Consent("ConsentNotify", GroupConsent.TerminalNotify, "ConsentTerminalNotify");
        Consent("ConsentPrompt", GroupConsent.TerminalPrompt, "ConsentTerminalPrompt");
        panel.Children.Add(Section("ConsentFiles"));
        Consent("ConsentNotify", GroupConsent.FilesNotify, "ConsentFilesNotify");
        Consent("ConsentPrompt", GroupConsent.FilesPrompt, "ConsentFilesPrompt");
        panel.Children.Add(Section("ConsentRegistry"));
        Consent("ConsentNotify", GroupConsent.RegistryNotify, "ConsentRegistryNotify");
        Consent("ConsentPrompt", GroupConsent.RegistryPrompt, "ConsentRegistryPrompt");

        panel.Children.Add(Section("Features"));
        _record.IsChecked = s.HasFeature(GroupFeature.Record);
        _sync.IsChecked = s.HasFeature(GroupFeature.SyncName);
        _prefer.IsChecked = s.HasFeature(GroupFeature.PreferAgentName);
        _override.IsChecked = s.HasFeature(GroupFeature.AllowOverride);
        _onDisconnect.IsChecked = s.HasFeature(GroupFeature.RemoveOnDisconnect);
        _inactive.IsChecked = s.RemoveInactiveDays > 0;
        _days.Text = (s.RemoveInactiveDays > 0 ? s.RemoveInactiveDays : 30).ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (server.GroupsChooseRecording) Add(panel, _record);
        Add(panel, _sync);
        if (g.Kind == DeviceGroupKind.Agents) { panel.Children.Add(_prefer); panel.Children.Add(_override); }
        if (g.Kind is DeviceGroupKind.Agents or DeviceGroupKind.IpKvm) Add(panel, _onDisconnect);
        Add(panel, _inactive);
        var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 4, 0, 0) };
        days.Children.Add(new TextBlock { Text = L.T("FeatDays"), VerticalAlignment = VerticalAlignment.Center });
        days.Children.Add(_days);
        days.Children.Add(_daysError);
        panel.Children.Add(days);

        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 100, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_save); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // As the website: ticking the sync ticks its two options. / Como la web: marcar la sincronización marca sus dos opciones.
        _sync.Checked += (_, _) => { _prefer.IsChecked = true; _override.IsChecked = true; Update(); };
        foreach (var b in new[] { _sync, _onDisconnect, _inactive }) { b.Checked += (_, _) => Update(); b.Unchecked += (_, _) => Update(); }
        _days.TextChanged += (_, _) => Update();
        _save.Click += (_, _) => { Result = Read(); DialogResult = true; };
        Update();
    }

    private static void Add(Panel p, CheckBox b) { b.Margin = new Thickness(0, 4, 0, 0); p.Children.Add(b); }

    private int? Days => int.TryParse(_days.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.CurrentCulture, out var d) ? d : null;

    private void Update()
    {
        var sync = _sync.IsChecked == true;
        _prefer.IsEnabled = _override.IsEnabled = sync;
        if (!sync) { _prefer.IsChecked = false; _override.IsChecked = false; }
        var onDisconnect = _onDisconnect.IsChecked == true;
        _inactive.IsEnabled = !onDisconnect;
        if (onDisconnect) _inactive.IsChecked = false;
        var inactive = _inactive.IsChecked == true;
        _days.IsEnabled = inactive;
        var ok = !inactive || Days is >= 1 and <= GroupSettings.MaxDays;
        _daysError.Visibility = ok ? Visibility.Hidden : Visibility.Visible;
        _save.IsEnabled = ok;
    }

    private GroupSettings Read()
    {
        var consent = _consent.Where(c => c.Box.IsChecked == true).Aggregate(0, (a, c) => a | c.Bit);
        var flags = 0;
        if (_record.IsChecked == true && _server.GroupsChooseRecording) flags |= (int)GroupFeature.Record;
        if (_sync.IsChecked == true) flags |= (int)GroupFeature.SyncName;
        if (_prefer.IsChecked == true) flags |= (int)GroupFeature.PreferAgentName;
        if (_override.IsChecked == true) flags |= (int)GroupFeature.AllowOverride;
        if (_onDisconnect.IsChecked == true) flags |= (int)GroupFeature.RemoveOnDisconnect;
        var days = _inactive.IsChecked == true && _onDisconnect.IsChecked != true ? Days ?? 0 : 0;
        return new GroupSettings(consent, flags, days);
    }

    /// <summary>The settings to save, or null if cancelled. / Los ajustes a guardar, o null si se cancela.</summary>
    public static GroupSettings? Ask(Window owner, DeviceGroup g, ServerInfo server)
    {
        var w = new GroupSettingsWindow(g, server) { Owner = owner };
        return w.ShowDialog() == true ? w.Result : null;
    }

    /// <summary>The group's consent and features in one line, as the website lists them. / En una línea, como la web.</summary>
    public static string Summary(GroupSettings s, ServerInfo server)
    {
        var c = s.Consent | server.ForcedConsent;
        var parts = new List<string>();
        void Tool(string name, GroupConsent notify, GroupConsent prompt, GroupConsent toolbar = GroupConsent.None)
        {
            var what = new List<string>();
            if ((c & (int)prompt) != 0) what.Add(L.T("ConsentPromptShort")); else if ((c & (int)notify) != 0) what.Add(L.T("ConsentNotifyShort"));
            if (toolbar != GroupConsent.None && (c & (int)toolbar) != 0) what.Add(L.T("ConsentToolbarShort"));
            if (what.Count > 0) parts.Add($"{L.T(name)}: {string.Join(", ", what)}");
        }
        Tool("ConsentDesktop", GroupConsent.DesktopNotify, GroupConsent.DesktopPrompt, GroupConsent.DesktopToolbar);
        Tool("ConsentTerminal", GroupConsent.TerminalNotify, GroupConsent.TerminalPrompt);
        Tool("ConsentFiles", GroupConsent.FilesNotify, GroupConsent.FilesPrompt);
        Tool("ConsentRegistry", GroupConsent.RegistryNotify, GroupConsent.RegistryPrompt);
        if (s.HasFeature(GroupFeature.Record) && server.GroupsChooseRecording) parts.Add(L.T("FeatRecord"));
        if (s.HasFeature(GroupFeature.SyncName)) parts.Add(L.T("FeatSyncShort"));
        if (s.HasFeature(GroupFeature.RemoveOnDisconnect)) parts.Add(L.T("FeatRemoveOnDisconnect"));
        if (s.RemoveInactiveDays > 0) parts.Add(L.F("FeatInactiveShort", s.RemoveInactiveDays));
        return parts.Count == 0 ? L.T("GroupSettingsNone") : string.Join(" · ", parts);
    }
}
