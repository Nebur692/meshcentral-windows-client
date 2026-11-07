using System.Windows;
using System.Windows.Controls;

namespace MeshCentralClient.App;

/// <summary>A small dialog for errors that do not belong to the login panel.
/// Un diálogo pequeño para los errores que no son del panel de entrada.</summary>
public static class ErrorWindow
{
    public static void Show(Window? owner, UserError error)
    {
        var details = new Expander
        {
            Header = L.T("Details"),
            Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBox { Text = error.Technical, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200,
                                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") },
        };
        var copy = new Button { Content = L.T("Copy"), Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = L.T("Close"), IsDefault = true, IsCancel = true };
        var w = new Window
        {
            Title = L.T("AppTitle"), Owner = owner, SizeToContent = SizeToContent.Height, Width = 480,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        copy.Click += (_, _) => { try { Clipboard.SetText(error.CopyText); } catch (Exception) { } };
        close.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, FontSize = 15 },
                new TextBlock { Text = error.ActLine, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = error.Footer, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.75 },
                details,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { copy, close } },
            },
        };
        w.ShowDialog();
    }
}
