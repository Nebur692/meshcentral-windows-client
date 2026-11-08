namespace MeshCentralClient.Core;

/// <summary>
/// Work that must not overlap, done one after another in the order it was asked for, without the caller waiting: keys
/// sent while the one before is still going. A failure reaches only its own caller; the next one still runs.
/// Trabajo que no puede solaparse, hecho uno tras otro en el orden pedido, sin que quien llama espere: teclas mandadas
/// mientras aún sale la anterior. Un fallo solo le llega a quien lo pidió; lo siguiente se hace igual.
/// </summary>
public sealed class InOrder
{
    private readonly object _gate = new();
    private Task _last = Task.CompletedTask;

    public Task Run(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var before = _last;
            return _last = Next();
            async Task Next()
            {
                try { await before.ConfigureAwait(false); }
                catch (Exception) { } // told to whoever asked for that one / ya se le dijo a quien lo pidió
                await work().ConfigureAwait(false);
            }
        }
    }
}
