using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>Asks whether to trust a certificate Windows does not trust. / Pregunta si confiar en un certificado en el que Windows no confía.</summary>
public static class CertificateWindow
{
    public static bool Ask(Window owner, string host, string? subject, string sha256, bool changed)
    {
        var trust = new Button { Content = L.T("CertTrust"), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Cancel"), IsDefault = true, IsCancel = true };
        var w = new Window
        {
            Title = L.T("CertTitle") + " — " + host, Owner = owner, SizeToContent = SizeToContent.Height, Width = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        var accepted = false;
        trust.Click += (_, _) => { accepted = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        var panel = new StackPanel { Margin = new Thickness(20) };
        if (changed)
            panel.Children.Add(new TextBlock { Text = L.T("CertChanged"), Foreground = (Brush)Application.Current.Resources["ErrorBrush"], FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = L.T("CertIntro"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.T("CertCompare"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("CertSubject"), Opacity = 0.7 });
        panel.Children.Add(new TextBox { Text = subject ?? "?", IsReadOnly = true, Margin = new Thickness(0, 2, 0, 8) });
        panel.Children.Add(new TextBlock { Text = L.T("CertFingerprint"), Opacity = 0.7 });
        panel.Children.Add(new TextBox { Text = CertificateTrust.Display(sha256), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { trust, cancel } });
        w.Content = panel;
        w.ShowDialog();
        return accepted;
    }
}
