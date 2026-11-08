using MeshCentralClient.Core;
using Xunit;

/// <summary>Keys sent without waiting never overlap and keep their order. / Las teclas mandadas sin esperar no se solapan y conservan su orden.</summary>
public class InOrderTests
{
    [Fact]
    public async Task The_next_one_does_not_start_until_the_one_before_has_finished()
    {
        var order = new InOrder();
        var gate = new TaskCompletionSource();
        var started = new List<int>();
        var first = order.Run(async () => { started.Add(1); await gate.Task; started.Add(10); });
        var second = order.Run(() => { started.Add(2); return Task.CompletedTask; });
        await Task.Delay(100);
        Assert.Equal(new[] { 1 }, started);
        Assert.False(second.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { 1, 10, 2 }, started);
    }

    [Fact]
    public async Task A_failure_reaches_only_its_caller_and_the_next_one_runs()
    {
        var order = new InOrder();
        var ran = false;
        var bad = order.Run(() => throw new InvalidOperationException("no"));
        var good = order.Run(() => { ran = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
        await good;
        Assert.True(ran);
    }

    [Fact]
    public async Task Many_at_once_keep_the_order_they_were_asked_in()
    {
        var order = new InOrder();
        var seen = new List<int>();
        var all = Enumerable.Range(0, 500).Select(i => order.Run(async () => { if (i % 50 == 0) await Task.Delay(1); seen.Add(i); })).ToArray();
        await Task.WhenAll(all);
        Assert.Equal(Enumerable.Range(0, 500), seen);
    }
}
