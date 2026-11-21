using AlertApi.Application.Runs;
using AlertApi.Domain.Models;

namespace AlertApi.Tests.Application;

public sealed class AutoStartPolicyTests
{
    [Theory]
    [InlineData(Risk.ReadOnly, false, null, RunStartMode.Immediately)]
    [InlineData(Risk.ReadOnly, true, "approval", RunStartMode.Immediately)]
    [InlineData(Risk.Auto, true, "auto", RunStartMode.AfterCancelWindow)]
    [InlineData(Risk.Auto, true, "approval", null)]
    [InlineData(Risk.Auto, true, null, null)]
    [InlineData(Risk.Auto, false, "auto", null)]
    [InlineData(Risk.Approval, true, "auto", null)]
    public void Decide_StartsByItselfOnlyWhenEveryGateAllowsIt(Risk risk, bool accepted, string? startMode, RunStartMode? expected)
    {
        Assert.Equal(expected, AutoStartPolicy.Decide(risk, accepted, startMode));
    }
}
