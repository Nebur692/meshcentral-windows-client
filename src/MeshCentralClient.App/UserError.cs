using MeshCentralClient.Core;

namespace MeshCentralClient.App;

/// <summary>
/// What a person sees when something fails: what happened in plain words, whether they need to do anything,
/// and the footer with the burburito code. The technical part goes folded under "Details".
/// Lo que ve una persona cuando algo falla: qué ha pasado en cristiano, si tiene que hacer algo, y el pie con
/// el código burburito. Lo técnico va plegado en «Detalles».
/// </summary>
public sealed record UserError(string Message, bool MustAct, string Footer, string Technical)
{
    public string ActLine => L.F("DoYouAct", L.T(MustAct ? "Yes" : "No"));

    public string CopyText => $"{Message}\n{ActLine}\n{Footer}\n\n{Technical}";

    /// <summary>What a failure means when its kind alone would say something false: not every "refused" is a login.
    /// Lo que significa un fallo cuando su tipo diría algo falso: no todo «rechazado» es una entrada.</summary>
    private static (string En, string Es, bool Act)? ForThisAction(string key) => key switch
    {
        "tunel/ver-pantalla/dispositivo-mudo" => ("The device is not sending its screen. Close this window and open it again; if it goes on, restart the device's agent.",
            "El equipo no manda la imagen de su pantalla. Cierra esta ventana y vuelve a abrirla; si sigue, reinicia el agente del equipo.", true),
        "acciones/ejecutar-ordenes/no-autorizado" => ("You do not have the rights to run these commands on this device. As the signed-in user they also need terminal and files rights.",
            "No tienes permiso para ejecutar estas órdenes en este equipo. Como usuario con sesión piden además los de terminal y ficheros.", true),
        "acciones/ejecutar-ordenes/sin-conexion" => ("The device's agent is not connected.", "El agente del equipo no está conectado.", false),
        "acciones/ejecutar-ordenes/tiempo-agotado" => ("The commands did not finish in time. They must end by themselves and not ask for anything.",
            "Las órdenes no han acabado a tiempo. Tienen que acabar solas y no pedir nada.", true),
        "acciones/ejecutar-ordenes/conexion-cortada" => ("The connection to the device was cut while running them. If they ran as the signed-in user, check that someone is signed in there.",
            "Se ha cortado la conexión con el equipo al ejecutarlas. Si iban como usuario con sesión, comprueba que hay alguien con sesión allí.", true),
        "acciones/ejecutar-ordenes/respuesta-ilegible" => ("The device's PowerShell did not answer: nobody may be signed in there, or a policy may block it.",
            "El PowerShell del equipo no ha contestado: puede que no haya nadie con sesión, o que una directiva lo bloquee.", true),
        "acciones/ejecutar-ordenes/rechazo-remoto" => ("The device could not run the commands.", "El equipo no ha podido ejecutar las órdenes.", true),
        _ => null,
    };

    public static UserError From(MeshClientException e, string? serverVersion = null)
    {
        var (en, es, act) = ForThisAction(e.BurburitoKey) ?? e.Failure switch
        {
            MeshFailure.InvalidAddress => ("That is not a valid server address. Write it as in the browser, for example remote.example.com.",
                "Esa dirección de servidor no vale. Escríbela como en el navegador, por ejemplo remoto.ejemplo.com.", true),
            MeshFailure.InvalidCredentials => ("Wrong username or password.", "Usuario o contraseña incorrectos.", true),
            MeshFailure.TwoFactorRequired => ("The server asks for your second-factor code.", "El servidor pide tu código del segundo factor.", true),
            MeshFailure.InvalidTwoFactorCode => ("That code is not valid. Wait for the next one; if it keeps failing, check that the PC clock is right.",
                "Ese código no vale. Espera al siguiente; si sigue fallando, comprueba que la hora del PC está bien.", true),
            MeshFailure.TooManyAttempts => ("Too many failed attempts: the server is not letting this account or this PC in for a few minutes.",
                "Demasiados intentos fallidos: el servidor no deja entrar a esta cuenta o a este PC durante unos minutos.", true),
            MeshFailure.ToolsNotAllowed => ("This account is not allowed to use external tools. An administrator of the server has to allow it.",
                "Esta cuenta no puede usar herramientas externas. Tiene que permitirlo un administrador del servidor.", true),
            MeshFailure.EmailValidationRequired => ("Your email address is not verified yet. Log in once on the MeshCentral website and follow the link in the email.",
                "Tu correo aún no está verificado. Entra una vez en la web de MeshCentral y sigue el enlace del correo.", true),
            MeshFailure.UntrustedCertificate => ("The server's certificate is not trusted, so the connection was not opened.",
                "El certificado del servidor no es de confianza, así que no se ha abierto la conexión.", true),
            MeshFailure.NameNotResolved => ("That server name does not exist. Check the address and your internet connection.",
                "Ese nombre de servidor no existe. Revisa la dirección y tu conexión a internet.", true),
            MeshFailure.ServerUnreachable => ("Could not connect to the server. Try the same address in the browser: if it does not load either, the server or the network is down.",
                "No se ha podido conectar con el servidor. Prueba la misma dirección en el navegador: si tampoco carga, el servidor o la red están caídos.", true),
            MeshFailure.HttpError => ("The server answered, but it does not look like MeshCentral at that address (or its proxy is blocking web sockets).",
                "El servidor contesta, pero en esa dirección no parece haber un MeshCentral (o su proxy bloquea los web sockets).", true),
            MeshFailure.Rejected => ("The server refused the login for a reason this app does not know yet.",
                "El servidor ha rechazado la entrada por un motivo que esta app aún no conoce.", true),
            MeshFailure.Timeout => ("The server took too long to answer. Try again in a moment.",
                "El servidor ha tardado demasiado en contestar. Prueba otra vez dentro de un momento.", false),
            MeshFailure.ConnectionLost => ("The connection to the server was lost.", "Se ha cortado la conexión con el servidor.", false),
            MeshFailure.SessionExpired => ("The server did not let this account in. Sign in again; if it happens again, ask an administrator for access to this server in your sign-in provider.",
                "El servidor no ha dejado entrar a esta cuenta. Vuelve a entrar; si se repite, pide a un administrador acceso a este servidor en tu proveedor de inicio de sesión.", true),
            MeshFailure.NotFound => ("That file or folder does not exist on the device, or it cannot be opened.",
                "Ese fichero o carpeta no existe en el equipo, o no se puede abrir.", false),
            MeshFailure.UnexpectedResponse => ("The server answered something this app does not understand (maybe a newer MeshCentral version).",
                "El servidor ha contestado algo que esta app no entiende (quizá una versión de MeshCentral más nueva).", true),
            _ => ("Something failed.", "Algo ha fallado.", true),
        };
        var tech = $"{e.Failure}: {e.Message}";
        if (e.ServerMessage is not null) tech += $"\nServer: {e.ServerMessage}";
        if (e.HttpStatus is not null) tech += $"\nHTTP {e.HttpStatus}";
        if (serverVersion is not null) tech += $"\nMeshCentral {serverVersion}";
        if (e.InnerException is not null) tech += $"\n{e.InnerException.GetType().Name}: {e.InnerException.Message}";
        return new UserError(L.Spanish ? es : en, act, Burburito.Footer(e.BurburitoKey, L.Spanish), tech);
    }

    public static UserError Settings(bool corrupt, Exception ex) => corrupt
        ? new UserError(
            L.Spanish ? "No se pudo leer la lista de servidores guardados. La he apartado sin borrarla y empiezo con una vacía."
                      : "The list of saved servers could not be read. It has been set aside, not deleted, and the app starts with an empty one.",
            false, Burburito.Footer("ajustes/cambiar-ajustes/dato-corrupto", L.Spanish), ex.ToString())
        : new UserError(
            L.Spanish ? "No se pudo guardar la lista de servidores. Comprueba que queda espacio en el disco."
                      : "The list of servers could not be saved. Check there is free disk space.",
            true, Burburito.Footer("ajustes/cambiar-ajustes/permisos-fichero", L.Spanish), ex.ToString());

    public static UserError Update(MeshClientException e, Uri? page)
    {
        var where = page?.ToString() ?? "https://github.com/Nebur692/meshcentral-windows-client/releases";
        var (en, es, act) = e.BurburitoKey switch
        {
            "actualizar/desplegar/permisos-fichero" => ($"The app cannot replace itself in its folder (for example, Program Files). Move it to a folder of yours, or download the new version from {where}",
                $"La app no puede reemplazarse en su carpeta (por ejemplo, Archivos de programa). Muévela a una carpeta tuya o baja la versión nueva de {where}", true),
            "actualizar/desplegar/dato-corrupto" => ("The downloaded version does not match its published fingerprint, so it was NOT installed and nothing changed. Try again later.",
                "La versión descargada no coincide con su huella publicada, así que NO se ha instalado y no ha cambiado nada. Prueba más tarde.", false),
            "actualizar/desplegar/respuesta-ilegible" => ($"The published version is incomplete, so it was not installed. You can download it by hand from {where}",
                $"La versión publicada está incompleta, así que no se ha instalado. Puedes bajarla a mano de {where}", false),
            _ => ("The new version could not be downloaded. You can keep using this one and try again later.",
                "No se ha podido bajar la versión nueva. Puedes seguir usando esta y probar más tarde.", false),
        };
        return new UserError(L.Spanish ? es : en, act, Burburito.Footer(e.BurburitoKey, L.Spanish), $"{e.Failure}: {e.Message}\n{e.InnerException?.Message}");
    }

    public static UserError NeedPassword() => new(
        L.Spanish ? "Este servidor no ofrece inicio de sesión único: escribe tu usuario y tu contraseña."
                  : "This server does not offer single sign-on: type your username and password.",
        true, Burburito.Footer("conexion/iniciar-sesion/no-encontrado", L.Spanish), "No authStrategies on the login page.");

    public static UserError MissingWebView(Exception ex) => new(
        L.Spanish ? "Falta Microsoft Edge WebView2 Runtime, que esta función necesita. Instálalo desde https://go.microsoft.com/fwlink/p/?LinkId=2124703 y vuelve a abrirla."
                  : "Microsoft Edge WebView2 Runtime, which this feature needs, is missing. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and open it again.",
        true, Burburito.Footer("app/abrir-detalle/dependencia-caida", L.Spanish), ex.ToString());

    public static UserError Unexpected(Exception ex) => new(
        L.Spanish ? "Ha fallado algo que no esperábamos. Puedes seguir usando la app; si se repite, manda el texto de Detalles."
                  : "Something we did not expect failed. You can keep using the app; if it happens again, send the text under Details.",
        false, Burburito.Footer("app/sin-especificar/estado-imposible", L.Spanish), ex.ToString());
}
