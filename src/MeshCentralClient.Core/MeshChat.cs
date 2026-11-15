using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MeshCentralClient.Core;

/// <summary>What happens in a chat. / Lo que pasa en un chat.</summary>
public abstract record ChatEvent
{
    /// <summary>The person opened the chat page on the device; <paramref name="Recorded"/>: the server records it. / La persona abrió la página.</summary>
    public sealed record Joined(bool Recorded) : ChatEvent;
    /// <summary>A message from the person at the device. / Un mensaje de la persona del equipo.</summary>
    public sealed record Message(string Text) : ChatEvent;
    /// <summary>The person is writing (or stopped). / La persona está escribiendo (o dejó de hacerlo).</summary>
    public sealed record Typing(bool Writing) : ChatEvent;
}

/// <summary>
/// A chat with the person at a device, as the website's "Chat" opens it (measured on MeshCentral 1.3.1: meshuser.js
/// "meshmessenger", meshrelay.js and messenger.handlebars). The server asks the agent to open its chat page in the
/// device's browser, and both sides meet in the relay at "meshmessenger/&lt;node&gt;/&lt;user&gt;". The relay keeps a lonely
/// side only 15 s, so while the person has not opened the page this side keeps calling again, until the wait is over
/// (on time, not when the relay drops a call). Text goes as {"action":"chat"} and the "writing" sign as
/// {"action":"outtext"}; the page's WebRTC is never started (this side does not answer "random"), so everything stays
/// on the relay and the server can record it. The same as the Android app.
/// Un chat con la persona del equipo, como lo abre «Chat» en la web. El servidor pide al agente que abra su página de
/// chat en el navegador del equipo y los dos lados se encuentran en el relé. El relé solo guarda 15 s a un lado solo,
/// así que este lado vuelve a llamar hasta que acaba la espera (a su hora). Texto como «chat», «escribiendo» como
/// «outtext»; el WebRTC nunca arranca, así que todo va por el relé y el servidor puede grabarlo. Igual que en Android.
/// </summary>
public sealed class MeshChat : IAsyncDisposable
{
    public const int MaxText = 4000;
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Recall = TimeSpan.FromMilliseconds(300);
    private const string PersonLeftKey = "chat/recibir-mensaje/conexion-cortada";
    // Letters as letters: the server records the chat as text, and "\u00E9" there is not "é". / Las letras como letras: el servidor graba el texto.
    private static readonly System.Text.Json.JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly MeshServerSession _session;
    private readonly Uri _url;
    private readonly TimeSpan _wait;
    private readonly Channel<ChatEvent> _events = Channel.CreateUnbounded<ChatEvent>();
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _ended;

    private MeshChat(MeshServerSession session, string nodeId, Uri url, TimeSpan wait)
    {
        _session = session; NodeId = nodeId; _url = url; _wait = wait;
    }

    public string NodeId { get; }

    /// <summary>Everything the person does; it completes with the reason as its error (none after <see cref="DisposeAsync"/>).
    /// Todo lo que hace la persona; acaba con el motivo como error (ninguno tras cerrar).</summary>
    public ChannelReader<ChatEvent> Events => _events.Reader;

    /// <summary>
    /// The next thing the person does; null when the chat was closed here, and the reason (a
    /// <see cref="MeshClientException"/>) when it ended by itself. / Lo siguiente que hace la persona; null si se cerró
    /// aquí, y el motivo si acabó solo.
    /// </summary>
    public async Task<ChatEvent?> NextAsync(CancellationToken ct = default)
    {
        try
        {
            if (await _events.Reader.WaitToReadAsync(ct).ConfigureAwait(false) && _events.Reader.TryRead(out var e)) return e;
        }
        catch (MeshClientException) { throw; }
        catch (System.Threading.Channels.ChannelClosedException ex) when (ex.InnerException is MeshClientException m) { throw m; }
        return null;
    }

    /// <summary>The person is in the chat. / La persona está en el chat.</summary>
    public bool Joined { get; private set; }

    public bool IsOpen => Volatile.Read(ref _ended) == 0;

    /// <summary>The key a chat ends with when the person closes it (or the line drops), not a failure here.
    /// La clave con la que acaba un chat cuando la persona lo cierra, no un fallo de aquí.</summary>
    public static bool IsPersonLeft(MeshClientException e) => e.BurburitoKey == PersonLeftKey;

    /// <summary>Opens the chat page on the device and waits for the person in the relay. / Abre el chat en el equipo y espera.</summary>
    public static async Task<MeshChat> OpenAsync(MeshServerSession session, Device device, TimeSpan? wait = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(device);
        if ((session.RightsOn(device) & (long)MeshRights.ChatNotify) == 0)
            throw new MeshClientException(MeshFailure.Rejected, "chat/abrir-detalle/no-autorizado", "This account has no rights to chat with this device.");
        if (!device.HasAgent) throw new MeshClientException(MeshFailure.Rejected, "chat/abrir-detalle/sin-conexion", "The device is not connected.");
        var cookies = await session.GetRelayCookiesAsync(ct).ConfigureAwait(false);
        var id = $"meshmessenger/{device.Id}/{session.User.Id}";
        var url = new Uri($"{session.Server.WebSocketUri("meshrelay.ashx")}?id={Uri.EscapeDataString(id)}&auth={Uri.EscapeDataString(cookies.Cookie)}");
        var chat = new MeshChat(session, device.Id, url, wait ?? DefaultWait);
        // This side waits first, so the page always finds it. / Este lado espera primero, así la página siempre lo encuentra.
        _ = Task.Run(chat.CallAsync);
        try
        {
            await session.SendAsync(new JsonObject { ["action"] = "meshmessenger", ["nodeid"] = device.Id }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await chat.DisposeAsync().ConfigureAwait(false);
            throw new MeshClientException(MeshFailure.ConnectionLost, "chat/abrir-detalle/conexion-cortada", "The connection to the server was lost.", ex);
        }
        return chat;
    }

    /// <summary>Sends a message; false while the person has not joined. / Manda un mensaje; false si la persona aún no está.</summary>
    public async Task<bool> SayAsync(string text, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length is 0 or > MaxText)
            throw new MeshClientException(MeshFailure.Rejected, "chat/enviar-mensaje/dato-invalido", $"A message has from 1 to {MaxText} characters.");
        return await SendJsonAsync(new JsonObject { ["action"] = "chat", ["msg"] = t }, ct).ConfigureAwait(false);
    }

    /// <summary>Tells the person whether this side is writing. / Dice a la persona si este lado está escribiendo.</summary>
    public Task<bool> WritingAsync(bool on, CancellationToken ct = default) =>
        SendJsonAsync(new JsonObject { ["action"] = "outtext", ["value"] = on }, ct);

    private async Task<bool> SendJsonAsync(JsonObject o, CancellationToken ct)
    {
        var ws = _ws;
        if (!IsOpen || !Joined || ws is null) return false;
        try
        {
            await WholeMessage.SendAsync(ws, _sendLock, Encoding.UTF8.GetBytes(o.ToJsonString(Plain)), WebSocketMessageType.Text, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new MeshClientException(MeshFailure.ConnectionLost, "chat/enviar-mensaje/conexion-cortada", "The chat's connection was lost.", ex);
        }
    }

    /// <summary>Reads one message of the page; null for what this side does not use. / Lee un mensaje de la página.</summary>
    internal static ChatEvent? Parse(string text)
    {
        if (!text.StartsWith('{')) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
        if (o is null || o["ctrlChannel"] is not null) return null;
        return o.Str("action") switch
        {
            "chat" => o.Str("msg") is { } m ? new ChatEvent.Message(m) : null,
            "outtext" => new ChatEvent.Typing(o["value"] is JsonValue v && v.TryGetValue<bool>(out var b) && b),
            _ => null,
        };
    }

    /// <summary>Calls until the person joins or the wait is over, then reads the chat. / Llama hasta que entra la persona y lee el chat.</summary>
    private async Task CallAsync()
    {
        var deadline = DateTime.UtcNow + _wait;
        try
        {
            while (IsOpen && !Joined)
            {
                var ws = new ClientWebSocket();
                ws.Options.RemoteCertificateValidationCallback = _session.Trust.Validate;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                ws.Options.CollectHttpResponseDetails = true;
                _ws = ws;
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) { TimedOut(); return; }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
                // The wait ends on time, not when the relay drops this call. / La espera acaba a su hora, no cuando el relé corta.
                cts.CancelAfter(left);
                try
                {
                    await ws.ConnectAsync(_url, cts.Token).ConfigureAwait(false);
                    var hello = await ReceiveTextAsync(ws, cts.Token).ConfigureAwait(false);
                    if (hello is "c" or "cr")
                    {
                        Joined = true;
                        _events.Writer.TryWrite(new ChatEvent.Joined(hello == "cr"));
                        await ReadAsync(ws).ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) when (!_life.IsCancellationRequested) { TimedOut(); return; }
                catch (WebSocketException ex) when (ws.HttpStatusCode is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError)
                {
                    End(new MeshClientException(MeshFailure.Rejected, "chat/abrir-detalle/no-autorizado", $"The server refused the chat (HTTP {(int)ws.HttpStatusCode}).", ex));
                    return;
                }
                catch (Exception ex) when (ex is WebSocketException or IOException) { /* the relay dropped the call: call again / el relé cortó: otra vez */ }
                finally { if (!Joined) ws.Dispose(); }
                if (DateTime.UtcNow >= deadline) { TimedOut(); return; }
                await Task.Delay(Recall, _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* closed / cerrado */ }
    }

    private void TimedOut() => End(new MeshClientException(MeshFailure.Timeout, "chat/abrir-detalle/dispositivo-mudo",
        "Nobody opened the chat on the device: there may be nobody signed in, or its browser did not open."));

    private async Task ReadAsync(ClientWebSocket ws)
    {
        try
        {
            while (IsOpen)
            {
                var text = await ReceiveTextAsync(ws, _life.Token).ConfigureAwait(false);
                if (text is null) break;
                if (Parse(text) is { } e) _events.Writer.TryWrite(e);
            }
            End(new MeshClientException(MeshFailure.ConnectionLost, PersonLeftKey, "The chat ended: the person closed it or the connection was lost."));
        }
        catch (OperationCanceledException) { /* closed / cerrado */ }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            End(new MeshClientException(MeshFailure.ConnectionLost, PersonLeftKey, "The chat ended: the person closed it or the connection was lost.", ex));
        }
    }

    /// <summary>One whole text message, or null when the socket closes. / Un mensaje de texto entero, o null si se cierra.</summary>
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Text) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    private void End(MeshClientException reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _events.Writer.TryComplete(reason);
        _life.Cancel();
        try { _ws?.Abort(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _events.Writer.TryComplete();
        _life.Cancel();
        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        ws?.Dispose();
    }
}
