using StrongholdAssistant.Host;
using Xunit;

namespace StrongholdAssistant.Tests;

public sealed class MouseNudgeTests
{
    [Fact]
    public void SmallJitterAndZeroReportsDoNotCount()
    {
        var nudge = new MouseNudge();
        long now = 10_000;
        // 手抖：每 8 毫秒挪一两下，持续两秒
        for (int i = 0; i < 250; i++, now += 8) Assert.False(nudge.Add(i % 2 == 0 ? 1 : -1, i % 3 - 1, now));
        // 有的鼠标不动也在发报告
        for (int i = 0; i < 500; i++, now += 1) Assert.False(nudge.Add(0, 0, now));
    }

    [Fact]
    public void DeliberateMoveCounts()
    {
        var nudge = new MouseNudge();
        long now = 10_000;
        bool moved = false;
        for (int i = 0; i < 30 && !moved; i++, now += 8) moved = nudge.Add(12, -5, now);
        Assert.True(moved);
    }
}
