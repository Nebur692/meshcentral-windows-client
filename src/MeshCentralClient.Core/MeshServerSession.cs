using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshCentralClient.Core;

/// <summary>
/// A logged-in session on a MeshCentral server, over its control web socket (control.ashx), the same channel
/// the official web site and meshctrl use.
/// Una sesión abierta en un servidor MeshCentral, por su web socket de control (control.ashx), el mismo canal
/// que usan la web oficial y meshctrl.
/// </summary>
public sealed class MeshServerSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _waitersLock = new();
    private readonly List<Waiter> _waiters = new();
    private readonly TimeSpan _timeout;
    private Task? _receiveLoop;
    private int _closed;

    private MeshServerSession(ServerAddress server, ClientWebSocket ws, TimeSpan timeout, CertificateTrust trust)
    {
        Trust = trust;
        Server = server;
        _ws = ws;
        _timeout = timeout;
    }

    public ServerAddress Server { get; }

    /// <summary>The certificate decision of this session, reused by its tunnels. / La decisión sobre el certificado, que reutilizan sus túneles.</summary>
    internal CertificateTrust Trust { get; }

    internal TimeSpan Timeout => _timeout;
    public ServerInfo ServerInfo { get; private set; } = new("", "", null);
    public UserInfo User { get; private set; } = new("", "", false);

    /// <summary>Raised from the receive thread. / Se lanza desde el hilo de recepción.</summary>
    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Raised once when the session ends for any reason other than DisposeAsync.
    /// Se lanza una vez cuando la sesión acaba por cualquier motivo que no sea DisposeAsync.</summary>
    public event EventHandler<MeshClientException>? Closed;

    public bool IsOpen => _ws.State == WebSocketState.Open && Volatile.Read(ref _closed) == 0;

    public static async Task<MeshServerSession> ConnectAsync(
        ServerAddress server, PasswordCredentials credentials, CertificateTrust trust,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return await ConnectCoreAsync(server, trust, o => o.SetRequestHeader("x-meshauth", credentials.Header()),
            close => LoginFailure(close, credentials), cancellationToken, timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in with the session cookies ("xid", "xid.sig") MeshCentral gave a browser after a single sign-on
    /// (OIDC, SAML, Azure, Google…). The cookie alone does not prove anything: a refused account also gets one, so
    /// only an accepted control channel means the person is in.
    /// Entra con las cookies de sesión («xid», «xid.sig») que MeshCentral dio a un navegador tras un inicio de sesión
    /// único (OIDC, SAML, Azure, Google…). La cookie sola no prueba nada: una cuenta rechazada también la recibe, así
    /// que solo un canal de control aceptado significa que la persona está dentro.
    /// </summary>
    public static async Task<MeshServerSession> ConnectWithSessionCookiesAsync(
        ServerAddress server, IReadOnlyDictionary<string, string> cookies, CertificateTrust trust,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (!cookies.ContainsKey("xid"))
            throw new MeshClientException(MeshFailure.SessionExpired, "conexion/iniciar-sesion/sesion-cerrada", "There is no MeshCentral session to use.");
        return await ConnectCoreAsync(server, trust, o =>
            {
                var jar = new CookieContainer();
                foreach (var (name, value) in cookies) jar.Add(server.BaseUri, new Cookie(name, value, "/"));
                o.Cookies = jar;
            },
            SessionFailure, cancellationToken, timeout).ConfigureAwait(false);
    }

    private static async Task<MeshServerSession> ConnectCoreAsync(
        ServerAddress server, CertificateTrust trust, Action<ClientWebSocketOptions> authenticate,
        Func<JsonObject, MeshClientException> onClose, CancellationToken cancellationToken, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(trust);
        var limit = timeout ?? DefaultTimeout;

        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = trust.Validate;
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.SetRequestHeader("User-Agent", "MeshCentralWindowsClient/" + typeof(MeshServerSession).Assembly.GetName().Version?.ToString(3));
        authenticate(ws.Options);

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(limit);
        try
        {
            await ws.ConnectAsync(server.ControlUri, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            ws.Dispose();
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw ConnectFailure(ex, ws, trust, connectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
        }

        var session = new MeshServerSession(server, ws, limit, trust);
        try
        {
            // Register before the loop starts, so the first messages cannot be missed.
            // Se apunta antes de arrancar el bucle, para no perder los primeros mensajes.
            using var loginCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loginCts.CancelAfter(limit);
            var serverInfo = session.WaitFor(o => o.Str("action") is "serverinfo" or "close", loginCts.Token);
            var userInfo = session.WaitFor(o => o.Str("action") is "userinfo" or "close", loginCts.Token);
            session._receiveLoop = Task.Run(session.ReceiveLoopAsync);

            JsonObject first, user;
            try
            {
                first = await serverInfo.ConfigureAwait(false);
                if (first.Str("action") == "close") throw onClose(first);
                user = await userInfo.ConfigureAwait(false);
                if (user.Str("action") == "close") throw onClose(user);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MeshClientException(MeshFailure.Timeout, "conexion/iniciar-sesion/tiempo-agotado", "The server accepted the connection but did not answer the login in time.");
            }

            session.ServerInfo = ServerInfo.From(first["serverinfo"] as JsonObject ?? new JsonObject());
            session.User = UserInfo.From(user["userinfo"] as JsonObject ?? new JsonObject());
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<DeviceGroup>> GetDeviceGroupsAsync(CancellationToken cancellationToken = default)
    {
        // "meshes" does not echo responseid. / «meshes» no devuelve el responseid.
        var reply = await RequestAsync(new JsonObject { ["action"] = "meshes" },
            o => o.Str("action") == "meshes", "equipos/ver-pantalla/tiempo-agotado", "equipos/ver-pantalla/conexion-cortada", cancellationToken).ConfigureAwait(false);
        if (reply["meshes"] is not JsonArray arr) throw Unexpected("equipos/ver-pantalla/respuesta-ilegible", "meshes");
        return arr.OfType<JsonObject>().Select(DeviceGroup.From).ToList();
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var id = NewId();
        var reply = await RequestAsync(new JsonObject { ["action"] = "nodes", ["responseid"] = id },
            o => o.Str("action") == "nodes" && o.Str("responseid") == id, "equipos/ver-pantalla/tiempo-agotado", "equipos/ver-pantalla/conexion-cortada", cancellationToken).ConfigureAwait(false);
        if (reply["nodes"] is not JsonObject byGroup) throw Unexpected("equipos/ver-pantalla/respuesta-ilegible", "nodes");
        var list = new List<Device>();
        foreach (var (groupId, nodes) in byGroup)
            if (nodes is JsonArray a)
                list.AddRange(a.OfType<JsonObject>().Select(n => Device.From(n, groupId)));
        return list;
    }

    /// <summary>Cookies to open relay tunnels (desktop, terminal, files).
    /// Cookies para abrir túneles de relé (escritorio, terminal, ficheros).</summary>
    public async Task<RelayCookies> GetRelayCookiesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(new JsonObject { ["action"] = "authcookie" },
            o => o.Str("action") == "authcookie", "conexion/abrir-detalle/tiempo-agotado", "conexion/abrir-detalle/conexion-cortada", cancellationToken).ConfigureAwait(false);
        var c = reply.Str("cookie");
        var r = reply.Str("rcookie");
        if (c is null || r is null) throw Unexpected("conexion/abrir-detalle/respuesta-ilegible", "authcookie");
        return new RelayCookies(c, r);
    }

    private async Task<JsonObject> RequestAsync(JsonObject request, Func<JsonObject, bool> match, string timeoutKey, string lostKey, CancellationToken ct)
    {
        MeshClientException LostError(Exception? inner) => new(MeshFailure.ConnectionLost, lostKey, "The connection to the server was lost.", inner);
        if (!IsOpen) throw LostError(null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        cts.CancelAfter(_timeout);
        var wait = WaitFor(match, cts.Token);
        try
        {
            await SendAsync(request, cts.Token).ConfigureAwait(false);
            return await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!IsOpen) throw LostError(null);
            throw new MeshClientException(MeshFailure.Timeout, timeoutKey, $"The server did not answer \"{request.Str("action")}\" in {_timeout.TotalSeconds:0} s.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
                                   || ex is MeshClientException { Failure: MeshFailure.ConnectionLost })
        {
            throw LostError(ex);
        }
    }

    internal async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private Task<JsonObject> WaitFor(Func<JsonObject, bool> match, CancellationToken ct)
    {
        var w = new Waiter(match);
        lock (_waitersLock) _waiters.Add(w);
        if (ct.CanBeCanceled)
        {
            var reg = ct.Register(() => { Remove(w); w.Tcs.TrySetCanceled(ct); });
            w.Tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        }
        return w.Tcs.Task;
    }

    private void Remove(Waiter w) { lock (_waitersLock) _waiters.Remove(w); }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        MeshClientException? reason = null;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _life.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text) Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { reason = Lost(ex); }
        catch (IOException ex) { reason = Lost(ex); }
        finally
        {
            reason ??= Lost(null);
            Shutdown(reason, raise: !_life.IsCancellationRequested);
        }
    }

    private static MeshClientException Lost(Exception? ex) =>
        new(MeshFailure.ConnectionLost, "conexion/ver-pantalla/conexion-cortada", "The connection to the server was closed.", ex);

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(utf8) as JsonObject; }
        catch (JsonException) { return; }
        if (o is null) return;

        List<Waiter> hit;
        lock (_waitersLock)
        {
            hit = _waiters.Where(w => SafeMatch(w, o)).ToList();
            foreach (var w in hit) _waiters.Remove(w);
        }
        foreach (var w in hit) w.Tcs.TrySetResult(o);

        if (o.Str("action") == "event" && o["event"] is JsonObject e) OnEvent(e);
    }

    private static bool SafeMatch(Waiter w, JsonObject o)
    {
        try { return w.Match(o); } catch (Exception) { return false; }
    }

    private void OnEvent(JsonObject e)
    {
        var handler = DeviceChanged;
        if (handler is null) return;
        DeviceChangedEventArgs? args = e.Str("action") switch
        {
            "addnode" when e["node"] is JsonObject n => new(DeviceChangeKind.Added, n.Str("_id") ?? "", Device.From(n)),
            "changenode" when e["node"] is JsonObject n => new(DeviceChangeKind.Changed, n.Str("_id") ?? "", Device.From(n)),
            "removenode" => new(DeviceChangeKind.Removed, e.Str("nodeid") ?? "", null),
            "nodeconnect" => new(DeviceChangeKind.ConnectionChanged, e.Str("nodeid") ?? "",
                new Device(e.Str("nodeid") ?? "", e.Str("meshid") ?? "", "", null, null, 0,
                    (DeviceConnectivity)(e.Long("conn") ?? 0), (int)(e.Long("pwr") ?? 0), AgentCapabilities.None, Array.Empty<string>())),
            _ => null,
        };
        if (args is null || args.DeviceId.Length == 0) return;
        try { handler(this, args); } catch (Exception) { /* a subscriber must not kill the session / un suscriptor no puede tumbar la sesión */ }
    }

    private void Shutdown(MeshClientException reason, bool raise)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        List<Waiter> pending;
        lock (_waitersLock) { pending = _waiters.ToList(); _waiters.Clear(); }
        // Whoever is still waiting gets the reason instead of hanging. / Al que sigue esperando se le da el motivo en vez de dejarlo colgado.
        foreach (var w in pending) w.Tcs.TrySetException(reason);
        if (raise) { try { Closed?.Invoke(this, reason); } catch (Exception) { } }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_life.IsCancellationRequested) _life.Cancel();
        if (_ws.State == WebSocketState.Open)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false); }
            catch (Exception) { }
        }
        if (_receiveLoop is not null) { try { await _receiveLoop.ConfigureAwait(false); } catch (Exception) { } }
        Shutdown(Lost(null), raise: false);
        _ws.Dispose();
        _sendLock.Dispose();
        _life.Dispose();
    }

    private static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    private static MeshClientException Unexpected(string key, string what) =>
        new(MeshFailure.UnexpectedResponse, key, $"The server sent an unexpected \"{what}\" reply.");

    internal static MeshClientException LoginFailure(JsonObject close, PasswordCredentials credentials)
    {
        var msg = close.Str("msg") ?? "";
        var cause = close.Str("cause") ?? "";
        var sentCode = !string.IsNullOrEmpty(credentials.TwoFactorCode);
        MeshClientException E(MeshFailure f, string key, string text) => new(f, key, text)
        {
            ServerMessage = string.IsNullOrEmpty(msg) ? cause : msg,
            EmailCodeAvailable = close.Bool("email2fa"),
            SmsCodeAvailable = close.Bool("sms2fa"),
            EmailCodeSent = close.Bool("email2fasent"),
        };
        return (cause, msg) switch
        {
            (_, "tokenrequired") when sentCode && credentials.TwoFactorCode is not "**email**" and not "**sms**" =>
                E(MeshFailure.InvalidTwoFactorCode, "conexion/verificar-identidad/credencial-invalida", "The second-factor code is not valid."),
            (_, "tokenrequired") => E(MeshFailure.TwoFactorRequired, "conexion/verificar-identidad/falta-dato", "The server asks for a second-factor code."),
            ("banned", _) or ("locked", _) => E(MeshFailure.TooManyAttempts, "conexion/iniciar-sesion/limite-de-tasa", "Too many failed attempts: the server is refusing logins for a while."),
            ("notools", _) => E(MeshFailure.ToolsNotAllowed, "conexion/iniciar-sesion/no-autorizado", "This account is not allowed to use tools like this one."),
            ("emailvalidation", _) => E(MeshFailure.EmailValidationRequired, "conexion/iniciar-sesion/falta-dato", "The account's email address has not been verified yet."),
            ("noauth", _) => E(MeshFailure.InvalidCredentials, "conexion/iniciar-sesion/credencial-invalida", "Wrong username or password."),
            _ => E(MeshFailure.Rejected, "conexion/iniciar-sesion/rechazo-remoto", $"The server refused the login ({cause} {msg})."),
        };
    }

    internal static MeshClientException SessionFailure(JsonObject close)
    {
        var cause = close.Str("cause") ?? "";
        var msg = close.Str("msg") ?? "";
        return (cause, msg) switch
        {
            ("notools", _) => new MeshClientException(MeshFailure.ToolsNotAllowed, "conexion/iniciar-sesion/no-autorizado", "This account is not allowed to use tools like this one.") { ServerMessage = msg },
            ("banned", _) or ("locked", _) => new MeshClientException(MeshFailure.TooManyAttempts, "conexion/iniciar-sesion/limite-de-tasa", "The server is refusing logins for a while.") { ServerMessage = msg },
            ("noauth", _) => new MeshClientException(MeshFailure.SessionExpired, "conexion/iniciar-sesion/sesion-cerrada",
                "The server did not accept the session: the sign-on was refused or the session has expired.") { ServerMessage = msg },
            _ => new MeshClientException(MeshFailure.Rejected, "conexion/iniciar-sesion/rechazo-remoto", $"The server refused the session ({cause} {msg}).") { ServerMessage = msg },
        };
    }

    private static MeshClientException ConnectFailure(Exception ex, ClientWebSocket ws, CertificateTrust trust, bool timedOut)
    {
        if (timedOut)
            return new(MeshFailure.Timeout, "conexion/iniciar-sesion/tiempo-agotado", "The server did not answer in time.", ex);
        if (trust.RejectedSubject is not null || trust.RejectedSha256 is not null)
            return new(MeshFailure.UntrustedCertificate, "conexion/iniciar-sesion/tls", "The server's certificate is not trusted.", ex)
            {
                CertificateSha256 = trust.RejectedSha256,
                CertificateSubject = trust.RejectedSubject,
            };
        var status = (int)ws.HttpStatusCode;
        if (status is >= 300 and not 101)
            return new(MeshFailure.HttpError, "conexion/iniciar-sesion/error-remoto", $"The server answered HTTP {status}: it may not be a MeshCentral server, or the address is wrong.", ex)
            {
                HttpStatus = status,
            };
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
                return new(MeshFailure.NameNotResolved, "conexion/iniciar-sesion/dns", "The server name could not be found.", ex);
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData })
                return new(MeshFailure.NameNotResolved, "conexion/iniciar-sesion/dns", "The server name could not be found.", ex);
        }
        return new(MeshFailure.ServerUnreachable, "conexion/iniciar-sesion/sin-conexion", "Could not connect to the server.", ex);
    }

    private sealed class Waiter(Func<JsonObject, bool> match)
    {
        public Func<JsonObject, bool> Match { get; } = match;
        public TaskCompletionSource<JsonObject> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
