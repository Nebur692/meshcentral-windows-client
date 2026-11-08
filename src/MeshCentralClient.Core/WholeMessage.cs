using System.Net.WebSockets;

namespace MeshCentralClient.Core;

/// <summary>
/// The one way this app sends on a WebSocket. Cancelling a WebSocket send half-way aborts the whole socket, so a person
/// who cancels a download (or any request) would lose the connection with it. The caller's token only decides whether
/// the message starts: while it waits for its turn, and just before it goes. Once started, it is sent whole.
/// La única forma en que la app envía por un WebSocket. Cancelar un envío a medias aborta el socket entero, así que quien
/// cancela una descarga (o cualquier petición) perdería la conexión con ella. El token de quien llama solo decide si el
/// mensaje empieza: mientras espera su turno y justo antes de salir. Una vez empezado, sale entero.
/// </summary>
internal static class WholeMessage
{
    public static async Task SendAsync(WebSocket ws, SemaphoreSlim turn, ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            await ws.SendAsync(data, type, true, CancellationToken.None).ConfigureAwait(false);
        }
        finally { turn.Release(); }
    }
}
