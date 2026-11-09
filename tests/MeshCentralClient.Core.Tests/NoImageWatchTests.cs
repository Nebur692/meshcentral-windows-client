using MeshCentralClient.Core;
using Xunit;

namespace MeshCentralClient.Core.Tests;

public class NoImageWatchTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task With_nothing_ever_coming_it_asks_each_time_and_then_gives_up()
    {
        var asked = 0;
        Assert.True(await NoImageWatch.GaveUpAsync(() => false, () => { asked++; return Task.CompletedTask; }, 5, Soon, default));
        Assert.Equal(5, asked);
    }

    [Fact]
    public async Task An_image_at_the_very_start_asks_nothing() 
    {
        var asked = 0;
        Assert.False(await NoImageWatch.GaveUpAsync(() => true, () => { asked++; return Task.CompletedTask; }, 5, Soon, default));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task What_the_asking_brings_stops_it_and_is_not_giving_up()
    {
        var asked = 0;
        Assert.False(await NoImageWatch.GaveUpAsync(() => asked >= 2, () => { asked++; return Task.CompletedTask; }, 5, Soon, default));
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task What_the_last_asking_brings_still_counts()
    {
        var asked = 0;
        Assert.False(await NoImageWatch.GaveUpAsync(() => asked == 5, () => { asked++; return Task.CompletedTask; }, 5, Soon, default));
    }

    [Fact]
    public async Task Closing_the_desktop_is_not_giving_up()
    {
        using var closing = new CancellationTokenSource();
        await closing.CancelAsync();
        Assert.False(await NoImageWatch.GaveUpAsync(() => false, () => Task.CompletedTask, 5, Soon, closing.Token));
    }
}
