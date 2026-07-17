// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the OnCallConfig value object: the shared settings-to-config parser
/// (defaults, percent-to-fraction conversion, clamping) and the type-to-factor mapping used by every
/// on-call hours path.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Scheduling;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Models.Scheduling;

[TestFixture]
public class OnCallConfigTests
{
    [Test]
    public void FromSettings_EmptyDictionary_UsesDocumentedDefaults()
    {
        var config = OnCallConfig.FromSettings(new Dictionary<string, string>());

        config.Enabled.ShouldBeFalse();
        config.PresenceFactor.ShouldBe(1.0m);
        config.StandbyFactor.ShouldBe(0.0m);
        config.IncludeInPeriodCaps.ShouldBeFalse();
    }

    [Test]
    public void FromSettings_ParsesEnabledAndPercentsIntoFractions()
    {
        var config = OnCallConfig.FromSettings(new Dictionary<string, string>
        {
            [SettingKeys.WorktimeOnCallEnabled] = "true",
            [SettingKeys.WorktimeOnCallPresenceCountsPercent] = "100",
            [SettingKeys.WorktimeOnCallStandbyCountsPercent] = "25",
            [SettingKeys.WorktimeOnCallIncludeInPeriodCaps] = "false",
        });

        config.Enabled.ShouldBeTrue();
        config.PresenceFactor.ShouldBe(1.0m);
        config.StandbyFactor.ShouldBe(0.25m);
        config.IncludeInPeriodCaps.ShouldBeFalse();
    }

    [Test]
    public void FromSettings_ClampsOutOfRangePercents()
    {
        var config = OnCallConfig.FromSettings(new Dictionary<string, string>
        {
            [SettingKeys.WorktimeOnCallEnabled] = "true",
            [SettingKeys.WorktimeOnCallPresenceCountsPercent] = "150",
            [SettingKeys.WorktimeOnCallStandbyCountsPercent] = "-10",
        });

        config.PresenceFactor.ShouldBe(1.0m);
        config.StandbyFactor.ShouldBe(0.0m);
    }

    [Test]
    public void FactorFor_Disabled_ReturnsZeroForEveryType()
    {
        var config = new OnCallConfig(false, 1.0m, 0.25m, false);

        config.FactorFor(WorkChangeType.OnCallPresence).ShouldBe(0m);
        config.FactorFor(WorkChangeType.OnCallStandby).ShouldBe(0m);
    }

    [Test]
    public void FactorFor_Enabled_MapsPresenceAndStandbyAndZeroForOthers()
    {
        var config = new OnCallConfig(true, 1.0m, 0.25m, false);

        config.FactorFor(WorkChangeType.OnCallPresence).ShouldBe(1.0m);
        config.FactorFor(WorkChangeType.OnCallStandby).ShouldBe(0.25m);
        config.FactorFor(WorkChangeType.TravelWithin).ShouldBe(0m);
        config.FactorFor(WorkChangeType.CorrectionEnd).ShouldBe(0m);
    }
}
