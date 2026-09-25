// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroResultAggregator, the one reader of a macro run's OUTPUT messages: channel 1 is the result and its
/// last value wins, a non-zero value on channels 10-14 becomes a typed surcharge item, zero surcharges, other channels
/// and non-numeric messages are ignored, numbers are read with the invariant culture.
/// </summary>

using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

[TestFixture]
[SetCulture("de-DE")]
public class MacroResultAggregatorTests
{
    private static ResultMessage Message(int channel, string value) => new() { Type = channel, Message = value };

    [Test]
    public void ResultChannel_LastValueWins()
    {
        var result = MacroResultAggregator.Aggregate([Message(1, "5"), Message(1, "7.25")]);

        result.Success.ShouldBeTrue();
        result.ResultValue.ShouldBe(7.25m);
    }

    [Test]
    public void SurchargeChannels_NonZeroBecomeTypedItems_ZeroIsDropped()
    {
        var result = MacroResultAggregator.Aggregate([Message(10, "0"), Message(11, "2.5"), Message(14, "1")]);

        result.Surcharges.ShouldBe(new[]
        {
            new MacroSurchargeItem(SurchargeType.Weekend1, 2.5m),
            new MacroSurchargeItem(SurchargeType.Holiday, 1m)
        });
        result.ResultValue.ShouldBeNull();
    }

    [Test]
    public void UnknownChannelsAndNonNumericValues_AreIgnored()
    {
        var result = MacroResultAggregator.Aggregate([Message(99, "3"), Message(1, "abc"), Message(12, "x")]);

        result.Success.ShouldBeTrue();
        result.ResultValue.ShouldBeNull();
        result.Surcharges.ShouldBeEmpty();
    }
}
