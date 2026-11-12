using Xunit;

namespace AutoFixScout.Tests;

public class FixActionRoutingTests
{
    // Regression: these once fell through into the temp-file cleanup case instead of going to the executor.
    [Theory]
    [InlineData("service.restart")]
    [InlineData("device.disable_enable")]
    [InlineData("device.enable")]
    [InlineData("network.reset_dns")]
    [InlineData("network.renew_address")]
    [InlineData("device.rescan")]
    [InlineData("service.set_manual")]
    [InlineData("printer.clear_queue")]
    public void AdminActions_GoToTheExecutor(string action) => Assert.Contains(action, FixActions.ElevatedActions);

    [Theory]
    [InlineData("storage.clear_user_temp")]
    [InlineData("storage.empty_recycle_bin")]
    [InlineData("audio.unmute_microphone")]
    public void UserActions_NeverAskForAdmin(string action) => Assert.DoesNotContain(action, FixActions.ElevatedActions);
}
