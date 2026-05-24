using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ContextMenuModeServiceTests
{
    [Fact]
    public void Defaults_to_win11_when_hack_absent()
    {
        Assert.False(new ContextMenuModeService(new FakeRegistry()).IsClassic());
    }

    [Fact]
    public void SetClassic_true_then_false_round_trips()
    {
        var reg = new FakeRegistry();
        var svc = new ContextMenuModeService(reg);

        Assert.True(svc.SetClassic(true));    // changed
        Assert.True(svc.IsClassic());
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, ContextMenuModeService.InprocKey));

        Assert.True(svc.SetClassic(false));   // changed back
        Assert.False(svc.IsClassic());
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, ContextMenuModeService.ClsidKey));
    }

    [Fact]
    public void SetClassic_is_idempotent()
    {
        var svc = new ContextMenuModeService(new FakeRegistry());
        Assert.False(svc.SetClassic(false));  // already Win11 — no change
        Assert.True(svc.SetClassic(true));
        Assert.False(svc.SetClassic(true));   // already classic — no change
    }
}
