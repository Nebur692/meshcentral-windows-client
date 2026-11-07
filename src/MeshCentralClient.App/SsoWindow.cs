using System.Windows;
using MeshCentralClient.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MeshCentralClient.App;

/// <summary>
/// Single sign-on in the Windows browser (WebView2): the provider's own page, so the app never sees the password.
/// When the browser comes back to MeshCentral, its session cookies are tried on the control channel: only an
/// accepted channel means the person is in (a refused account also gets a cookie). The browser profile lives in
/// %LOCALAPPDATA%\MeshCentralClient\WebView2, so the provider may remember the person next time, as a browser does.
/// Inicio de sesión único en el navegador de Windows (WebView2): la página del propio proveedor, así que la app no ve
/// nunca la contraseña. Cuando el navegador vuelve a MeshCentral, se prueban sus cookies de sesión en el canal de
/// control: solo un canal aceptado significa que la persona ha entrado (una cuenta rechazada también recibe cookie).
/// El perfil del navegador vive en %LOCALAPPDATA%\MeshCentralClient\WebView2, así que el proveedor puede recordar a la
/// persona la próxima vez, como un navegador.
/// </summary>
public sealed class SsoWindow : Window
{
    private readonly ServerAddress _server;
    private readonly SingleSignOn _sso;
    private readonly CertificateTrust _trust;
    private readonly WebView2 _view = new();
    private bool _trying;

    public MeshServerSession? Session { get; private set; }
    public MeshClientException? Failure { get; private set; }

    public SsoWindow(Window owner, ServerAddress server, SingleSignOn sso, CertificateTrust trust)
    {
        _server = server;
        _sso = sso;
        _trust = trust;
        Owner = owner;
        Title = L.F("SsoTitle", sso.DisplayName, server.Host);
        Width = 520;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "SsoWindow");
        Content = _view;
        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>Removes what the browser keeps of this server (its MeshCentral session). / Quita lo que guarda el navegador de este servidor.</summary>
    public static async Task ForgetAsync(ServerAddress server)
    {
        try
        {
            var view = new WebView2();
            await view.EnsureCoreWebView2Async(await WebAssets.EnvironmentAsync());
            var cm = view.CoreWebView2.CookieManager;
            foreach (var c in await cm.GetCookiesAsync(server.BaseUri.ToString())) cm.DeleteCookie(c);
            view.Dispose();
        }
        catch (Exception) { /* nothing kept, or no WebView2: nothing to forget / nada guardado o sin WebView2: nada que olvidar */ }
    }

    private async Task StartAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(await WebAssets.EnvironmentAsync());
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Failure = new MeshClientException(MeshFailure.Rejected, "app/abrir-detalle/dependencia-caida", "WebView2 Runtime is missing.", ex);
            Close();
            return;
        }
        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Pop-ups of the provider open in this same window. / Las ventanas nuevas del proveedor se abren en esta misma.
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
        // A server with its own certificate: only the fingerprint the person accepted. / Un servidor con certificado propio: solo la huella aceptada.
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            var ok = _trust.PinnedSha256 is { } pin
                     && Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u) && u.Authority == _server.BaseUri.Authority
                     && CertificateTrust.Fingerprint(e.ServerCertificate.ToX509Certificate2()) == pin;
            e.Action = ok ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        core.NavigationCompleted += async (_, _) => await OnPageAsync(core);
        core.Navigate(new Uri(_server.BaseUri, _sso.Path).ToString());
    }

    private async Task OnPageAsync(CoreWebView2 core)
    {
        // Only a page of the MeshCentral server ends the sign-on; the provider's pages are left to the person.
        // Solo una página del servidor MeshCentral cierra el inicio de sesión; las del proveedor son cosa de la persona.
        if (_trying || !Uri.TryCreate(core.Source, UriKind.Absolute, out var at) || at.Authority != _server.BaseUri.Authority) return;
        if (at.AbsolutePath == new Uri(_server.BaseUri, _sso.Path).AbsolutePath) return; // still starting / aún arrancando
        _trying = true;
        var cookies = (await core.CookieManager.GetCookiesAsync(_server.BaseUri.ToString()))
            .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Value);
        try
        {
            Session = await MeshServerSession.ConnectWithSessionCookiesAsync(_server, cookies, _trust);
        }
        catch (MeshClientException ex)
        {
            Failure = ex;
            // A refused account: forget that useless session, so the next try starts clean.
            // Una cuenta rechazada: se olvida esa sesión inútil para que el siguiente intento empiece limpio.
            foreach (var c in await core.CookieManager.GetCookiesAsync(_server.BaseUri.ToString())) core.CookieManager.DeleteCookie(c);
        }
        Close();
    }
}
