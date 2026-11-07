using System.Globalization;
using System.Windows.Markup;

namespace MeshCentralClient.App;

/// <summary>Interface text in English and Spanish (Spain), chosen by the Windows display language.
/// Textos de la interfaz en inglés y español de España, según el idioma de Windows.</summary>
public static class L
{
    public static bool Spanish { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";

    private static readonly Dictionary<string, (string En, string Es)> Text = new()
    {
        ["AppTitle"] = ("MeshCentral Client", "Cliente de MeshCentral"),
        ["Server"] = ("Server", "Servidor"),
        ["ServerHint"] = ("e.g. remote.example.com", "p. ej. remoto.ejemplo.com"),
        ["Username"] = ("Username", "Usuario"),
        ["Password"] = ("Password", "Contraseña"),
        ["RememberPassword"] = ("Remember password on this PC", "Recordar la contraseña en este PC"),
        ["Connect"] = ("Connect", "Conectar"),
        ["Connecting"] = ("Connecting…", "Conectando…"),
        ["Cancel"] = ("Cancel", "Cancelar"),
        ["TwoFactorTitle"] = ("Second factor", "Segundo factor"),
        ["TwoFactorHelp"] = ("Type the code from your authenticator app.", "Escribe el código de tu app de autenticación."),
        ["TwoFactorEmailSent"] = ("We have asked the server to email you a code.", "Le hemos pedido al servidor que te mande un código por correo."),
        ["SendEmailCode"] = ("Email me a code", "Mandarme un código por correo"),
        ["Code"] = ("Code", "Código"),
        ["Forget"] = ("Forget this server", "Olvidar este servidor"),
        ["Search"] = ("Search devices", "Buscar equipos"),
        ["Refresh"] = ("Refresh", "Actualizar"),
        ["Disconnect"] = ("Disconnect", "Desconectar"),
        ["OpenInBrowser"] = ("Open in the browser", "Abrir en el navegador"),
        ["CopyName"] = ("Copy name", "Copiar el nombre"),
        ["Online"] = ("Online", "Conectado"),
        ["Offline"] = ("Offline", "Desconectado"),
        ["DevicesCount"] = ("Devices: {0} · online: {1}", "Equipos: {0} · conectados: {1}"),
        ["NoDevices"] = ("This account cannot see any device on this server.", "Esta cuenta no ve ningún equipo en este servidor."),
        ["Details"] = ("Details", "Detalles"),
        ["Copy"] = ("Copy", "Copiar"),
        ["Close"] = ("Close", "Cerrar"),
        ["CertTitle"] = ("Untrusted certificate", "Certificado no fiable"),
        ["CertIntro"] = ("Windows does not trust this server's certificate. That is normal for a MeshCentral with its own certificate, but it can also mean someone is in the middle.",
                         "Windows no confía en el certificado de este servidor. Es normal en un MeshCentral con certificado propio, pero también puede significar que alguien se ha metido en medio."),
        ["CertCompare"] = ("Only trust it if this fingerprint matches the one of your server (MeshCentral web, My Server).",
                           "Confía solo si esta huella coincide con la de tu servidor (web de MeshCentral, Mi servidor)."),
        ["CertChanged"] = ("WARNING: you had accepted a DIFFERENT certificate for this server.", "ATENCIÓN: para este servidor habías aceptado un certificado DISTINTO."),
        ["CertSubject"] = ("Issued to", "Emitido para"),
        ["CertFingerprint"] = ("SHA-256 fingerprint", "Huella SHA-256"),
        ["CertTrust"] = ("Trust this certificate", "Confiar en este certificado"),
        ["DoYouAct"] = ("Do you need to do anything? {0}", "¿Tienes que hacer algo? {0}"),
        ["Yes"] = ("Yes", "Sí"),
        ["No"] = ("No", "No"),
        ["ConnectionLostBanner"] = ("The connection to the server was lost.", "Se ha cortado la conexión con el servidor."),
        ["Reconnect"] = ("Reconnect", "Volver a conectar"),
        ["ColName"] = ("Name", "Nombre"),
        ["Terminal"] = ("Terminal", "Terminal"),
        ["UserShell"] = ("User terminal", "Terminal del usuario"),
        ["OpenTerminal"] = ("Open terminal", "Abrir terminal"),
        ["OpenPowerShell"] = ("Open PowerShell", "Abrir PowerShell"),
        ["TerminalHelp"] = ("Ctrl+Shift+C copies, Ctrl+Shift+V pastes; right click copies the selection or pastes.", "Ctrl+Mayús+C copia y Ctrl+Mayús+V pega; el clic derecho copia lo seleccionado o pega."),
        ["TerminalRecorded"] = ("This session is being recorded by the server.", "El servidor está grabando esta sesión."),
        ["Theme"] = ("Theme", "Tema"),
        ["Version"] = ("Version {0}", "Versión {0}"),
        ["UpdateAvailable"] = ("Version {0} is available.", "Hay una versión nueva: la {0}."),
        ["UpdateDownloading"] = ("Downloading version {0}…", "Descargando la versión {0}…"),
        ["UpdateNow"] = ("Update and restart", "Actualizar y reabrir"),
        ["UpdateLater"] = ("Later", "Más tarde"),
        ["ThemeSystem"] = ("Like Windows", "Como Windows"),
        ["ThemeLight"] = ("Light", "Claro"),
        ["ThemeDark"] = ("Dark", "Oscuro"),
        ["ColOs"] = ("Operating system", "Sistema operativo"),
        ["ColAddress"] = ("Address", "Dirección"),
        ["ColTags"] = ("Tags", "Etiquetas"),
    };

    public static string T(string key) => Text.TryGetValue(key, out var t) ? (Spanish ? t.Es : t.En) : key;

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}

/// <summary>XAML: Text="{app:T Connect}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
