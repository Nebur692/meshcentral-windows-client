using System.Windows.Input;

namespace MeshCentralClient.App;

/// <summary>
/// One key of the remote desktop, with what copy and paste need around it, in the order it has to happen: pasting there
/// waits until this PC's copy has arrived; copying there brings it here once the device has it. Apart from the window so
/// that order can be tested with the real queue.
/// Una tecla del escritorio remoto, con lo que necesita alrededor el copiar y pegar, en el orden en que tiene que pasar:
/// pegar allí espera a que haya llegado lo copiado aquí; copiar allí lo trae aquí cuando el equipo ya lo tiene. Aparte de la
/// ventana para poder probar ese orden con la cola de verdad.
/// </summary>
internal static class DesktopKeys
{
    internal static bool IsPaste(Key key, ModifierKeys mods, bool down) =>
        down && ((mods.HasFlag(ModifierKeys.Control) && key == Key.V) || (mods.HasFlag(ModifierKeys.Shift) && key == Key.Insert));

    /// <summary>
    /// On the press, not the release: whoever lets go of Ctrl first has no Ctrl left when the C goes up, and the copy never
    /// came back. A key held down repeats its press: only the first one counts.
    /// Al pulsar, no al soltar: quien suelta antes el Ctrl ya no tiene Ctrl cuando sube la C, y lo copiado no volvía nunca.
    /// Una tecla mantenida repite la pulsación: solo cuenta la primera.
    /// </summary>
    internal static bool IsCopy(Key key, ModifierKeys mods, bool down, bool repeat) =>
        down && !repeat && mods.HasFlag(ModifierKeys.Control) && key is Key.C or Key.X or Key.Insert;

    /// <summary>Queues the key and whatever goes with it. / Pone en la cola la tecla y lo que la acompaña.</summary>
    /// <summary>Ctrl+Alt+Pause, which Windows reports as Pause or as Cancel. / Ctrl+Alt+Pausa, que Windows da como Pause o como Cancel.</summary>
    internal static bool IsFullScreen(Key key, ModifierKeys mods) =>
        mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Alt) && key is Key.Pause or Key.Cancel;

    internal static void Send(Action<Func<Task>> queue, Key key, ModifierKeys mods, bool down, bool repeat, Func<Task> send, Func<Task>? beforePaste, Func<Task>? afterCopy)
    {
        if (beforePaste is not null && IsPaste(key, mods, down)) queue(beforePaste);
        queue(send);
        if (afterCopy is not null && IsCopy(key, mods, down, repeat)) queue(afterCopy);
    }
}
