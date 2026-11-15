using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeshCentralClient.Core;
using AP = System.Windows.Automation.AutomationProperties;

namespace MeshCentralClient.App;

/// <summary>One line of the chat: <see cref="Mine"/> is what this side wrote. / Una línea del chat.</summary>
public sealed record ChatLine(string Text, bool Mine)
{
    public override string ToString() => Text;
}

/// <summary>
/// The chat with the person at a device: it opens the chat page on the device, waits for the person (the core calls
/// again while waiting), and keeps the conversation; nothing is sent before the person is in. The same as the
/// Android app's chat screen.
/// El chat con la persona del equipo: abre la página en el equipo, espera a la persona y guarda la conversación; nada se
/// manda antes de que entre. Igual que la pantalla de chat de Android.
/// </summary>
public sealed class ChatWindow : Window
{
    private readonly MeshServerSession _session;
    private readonly Device _device;
    private readonly ObservableCollection<ChatLine> _lines = new();
    private readonly TextBlock _state = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recorded = new() { Text = L.T("ChatRecorded"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.75 };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _box = new() { MinHeight = 32, MaxHeight = 120, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MaxLength = MeshChat.MaxText, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _send = new() { Content = L.T("ChatSend"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
    private readonly Button _again = new() { Content = L.T("ChatAgain"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer _stopWriting = new() { Interval = TimeSpan.FromSeconds(4) };
    private MeshChat? _chat;
    private bool _writingSent;
    private bool _closing;

    public ChatWindow(MeshServerSession session, Device device)
    {
        _session = session; _device = device;
        Title = $"{L.T("ChatItem")} — {device.Name}";
        Width = 560; Height = 620; MinWidth = 380; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AP.SetAutomationId(this, "ChatWindow"); AP.SetAutomationId(_state, "ChatState"); AP.SetAutomationId(_list, "ChatLines");
        AP.SetAutomationId(_box, "ChatBox"); AP.SetAutomationId(_send, "ChatSend"); AP.SetAutomationId(_again, "ChatAgain");
        AP.SetName(_box, L.T("ChatHint"));

        // Mine in the accent colour, theirs in the control colour, both from the theme. / Las mías en color de acento, las suyas en el de los controles.
        _list.ItemTemplateSelector = new BubbleSelector(Bubble(false), Bubble(true));
        _list.ItemsSource = _lines;

        var top = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        top.Children.Add(_state);
        top.Children.Add(_recorded);
        top.Children.Add(_again);
        var bottom = new DockPanel { Margin = new Thickness(16, 8, 16, 16) };
        DockPanel.SetDock(_send, Dock.Right);
        bottom.Children.Add(_send);
        bottom.Children.Add(_box);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom);
        _list.Margin = new Thickness(16, 0, 16, 0);
        root.Children.Add(_list);
        Content = root;

        _send.Click += async (_, _) => await SendAsync();
        _again.Click += async (_, _) => await StartAsync();
        _box.TextChanged += async (_, _) => await TypedAsync();
        _stopWriting.Tick += async (_, _) => await StopWritingAsync();
        Loaded += async (_, _) => await StartAsync();
        Closed += async (_, _) => { _closing = true; _stopWriting.Stop(); if (_chat is { } c) await c.DisposeAsync(); };
    }

    private static DataTemplate Bubble(bool mine)
    {
        var bubble = new FrameworkElementFactory(typeof(Border));
        bubble.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        bubble.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 6));
        bubble.SetValue(Border.MarginProperty, new Thickness(0, 3, 0, 3));
        bubble.SetValue(Border.MaxWidthProperty, 380.0);
        bubble.SetValue(Border.HorizontalAlignmentProperty, mine ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        bubble.SetResourceReference(Border.BackgroundProperty, mine ? SystemColors.HighlightBrushKey : SystemColors.ControlBrushKey);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ChatLine.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetResourceReference(TextBlock.ForegroundProperty, mine ? SystemColors.HighlightTextBrushKey : SystemColors.ControlTextBrushKey);
        bubble.AppendChild(text);
        return new DataTemplate { VisualTree = bubble };
    }

    private sealed class BubbleSelector(DataTemplate theirs, DataTemplate mine) : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container) => item is ChatLine { Mine: true } ? mine : theirs;
    }

    private void Show(string state, bool talking)
    {
        _state.Text = state;
        _box.IsEnabled = _send.IsEnabled = talking;
        if (talking) _box.Focus();
    }

    private async Task StartAsync()
    {
        _again.Visibility = Visibility.Collapsed;
        _recorded.Visibility = Visibility.Collapsed;
        Show(L.T("ChatOpening"), false);
        try
        {
            var chat = _chat = await MeshChat.OpenAsync(_session, _device);
            Show(L.T("ChatWaiting"), false);
            while (!_closing)
            {
                var e = await chat.NextAsync();
                if (e is null) return; // closed here / cerrado aquí
                switch (e)
                {
                    case ChatEvent.Joined j:
                        _recorded.Visibility = j.Recorded ? Visibility.Visible : Visibility.Collapsed;
                        Show(L.T("ChatTalking"), true);
                        break;
                    case ChatEvent.Message m:
                        Add(new ChatLine(m.Text, false));
                        Show(L.T("ChatTalking"), true);
                        break;
                    case ChatEvent.Typing t:
                        Show(L.T(t.Writing ? "ChatWriting" : "ChatTalking"), true);
                        break;
                }
            }
        }
        catch (MeshClientException ex)
        {
            if (_closing) return;
            // Once in, the chat ending is the person closing it, not this app failing. / Ya dentro, acabar es que la persona cerró.
            if (MeshChat.IsPersonLeft(ex)) Show(L.T("ChatPersonLeft"), false);
            else
            {
                var err = UserError.From(ex, _session.ServerInfo.Version);
                Show($"{err.Message}  {err.ActLine}  {err.Footer}", false);
            }
            _again.Visibility = Visibility.Visible;
        }
    }

    private void Add(ChatLine line)
    {
        _lines.Add(line);
        _list.ScrollIntoView(line);
    }

    private async Task SendAsync()
    {
        var t = _box.Text.Trim();
        if (_chat is not { } c || t.Length == 0) return;
        try
        {
            if (await c.SayAsync(t)) { Add(new ChatLine(t, true)); _box.Clear(); await StopWritingAsync(); }
        }
        catch (MeshClientException ex) { ErrorWindow.Show(this, UserError.From(ex, _session.ServerInfo.Version)); }
    }

    /// <summary>The person sees "writing" while there is text and typing in the last seconds. / «Escribiendo» mientras se teclea.</summary>
    private async Task TypedAsync()
    {
        if (_chat is not { Joined: true } c) return;
        if (_box.Text.Trim().Length == 0) { await StopWritingAsync(); return; }
        if (!_writingSent) { _writingSent = true; try { await c.WritingAsync(true); } catch (MeshClientException) { } }
        _stopWriting.Stop(); _stopWriting.Start();
    }

    private async Task StopWritingAsync()
    {
        _stopWriting.Stop();
        if (!_writingSent || _chat is not { } c) return;
        _writingSent = false;
        try { await c.WritingAsync(false); } catch (MeshClientException) { }
    }
}
