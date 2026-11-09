using System.Windows.Input;
using MeshCentralClient.App;
using MeshCentralClient.Core;
using Xunit;

namespace MeshCentralClient.App.Tests;

public class DesktopKeysTests
{
    [Theory]
    [InlineData(Key.V, ModifierKeys.Control, true, true)]
    [InlineData(Key.Insert, ModifierKeys.Shift, true, true)]
    [InlineData(Key.V, ModifierKeys.Control | ModifierKeys.Shift, true, true)]
    [InlineData(Key.V, ModifierKeys.None, true, false)]   // a plain v / una v normal
    [InlineData(Key.V, ModifierKeys.Control, false, false)] // the release / al soltar
    [InlineData(Key.Insert, ModifierKeys.None, true, false)]
    public void What_counts_as_paste(Key key, ModifierKeys mods, bool down, bool expected) =>
        Assert.Equal(expected, DesktopKeys.IsPaste(key, mods, down));

    [Theory]
    [InlineData(Key.C, ModifierKeys.Control, true, false, true)]
    [InlineData(Key.X, ModifierKeys.Control, true, false, true)]
    [InlineData(Key.Insert, ModifierKeys.Control, true, false, true)]
    [InlineData(Key.C, ModifierKeys.None, true, false, false)]    // a plain c / una c normal
    [InlineData(Key.C, ModifierKeys.Control, true, true, false)]  // held down / mantenida
    [InlineData(Key.C, ModifierKeys.Control, false, false, false)] // the release, with Ctrl still down / al soltar, con Ctrl aún pulsado
    [InlineData(Key.C, ModifierKeys.None, false, false, false)]   // the release, with Ctrl already up / al soltar, con Ctrl ya suelto
    public void What_counts_as_copy(Key key, ModifierKeys mods, bool down, bool repeat, bool expected) =>
        Assert.Equal(expected, DesktopKeys.IsCopy(key, mods, down, repeat));

    /// <summary>The real queue of the window, and a copy that takes its time. / La cola real de la ventana, y una copia que tarda.</summary>
    [Fact]
    public async Task Ctrl_V_does_not_reach_the_device_until_the_copy_has_arrived()
    {
        var keys = new InOrder();
        var said = new List<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task last = Task.CompletedTask;
        void Queue(Func<Task> f) => last = keys.Run(f);
        Task Note(string what) { lock (said) said.Add(what); return Task.CompletedTask; }

        DesktopKeys.Send(Queue, Key.LeftCtrl, ModifierKeys.Control, true, false, () => Note("ctrl"), () => arrived.Task, null);
        DesktopKeys.Send(Queue, Key.V, ModifierKeys.Control, true, false, () => Note("v"), () => arrived.Task, null);
        DesktopKeys.Send(Queue, Key.V, ModifierKeys.Control, false, false, () => Note("v up"), () => arrived.Task, null);

        await Task.Delay(200);
        lock (said) Assert.Equal(new[] { "ctrl" }, said); // the V waits, and nothing passes it / la V espera, y nada la adelanta

        arrived.SetResult();
        await last;
        lock (said) Assert.Equal(new[] { "ctrl", "v", "v up" }, said);
    }

    [Fact]
    public async Task Copy_is_brought_after_the_key_has_gone_out_whichever_key_is_released_first()
    {
        var keys = new InOrder();
        var said = new List<string>();
        Task last = Task.CompletedTask;
        void Queue(Func<Task> f) => last = keys.Run(f);
        Task Note(string what) { lock (said) said.Add(what); return Task.CompletedTask; }

        DesktopKeys.Send(Queue, Key.C, ModifierKeys.Control, true, false, () => Note("c"), null, () => Note("bring"));
        // Ctrl released first: the C goes up with no Ctrl, and that must not bring it again. / Ctrl primero: la C sube sin Ctrl.
        DesktopKeys.Send(Queue, Key.C, ModifierKeys.None, false, false, () => Note("c up"), null, () => Note("bring"));
        await last;
        lock (said) Assert.Equal(new[] { "c", "bring", "c up" }, said);
    }
}
