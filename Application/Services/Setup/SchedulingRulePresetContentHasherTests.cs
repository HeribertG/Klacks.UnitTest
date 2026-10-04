// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Setup;

namespace Klacks.UnitTest.Application.Services.Setup;

[TestFixture]
public class SchedulingRulePresetContentHasherTests
{
    private const string HashWithoutDailyFrame = "CAF18461C61E18F0DD393385949DA2D359D9C2EF20DE5616558D01BB40BD2B89";
    private const string HashWithDailyFrame14 = "69C187828CBA0416BE21386ACAFD9BA388C0DB873ED37B1BB65D0BA989C68FDE";

    private static SchedulingRulePresetImportValues Preset(decimal? maxDailySpanHours) => new(
        "Homecare day",
        null, null, null, null, null, 40m, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
        "homecare",
        maxDailySpanHours);

    [Test]
    public void Compute_WithoutDailyFrame_KeepsTheHashPresetsHadBeforeTheFieldExisted()
    {
        SchedulingRulePresetContentHasher.Compute(Preset(null)).ShouldBe(HashWithoutDailyFrame);
    }

    [Test]
    public void Compute_WithDailyFrame_AppendsItAsLastField()
    {
        SchedulingRulePresetContentHasher.Compute(Preset(14m)).ShouldBe(HashWithDailyFrame14);
    }

    [Test]
    public void Compute_IgnoresTheIndustryClassification()
    {
        var other = Preset(null) with { Industry = "retail" };

        SchedulingRulePresetContentHasher.Compute(other).ShouldBe(HashWithoutDailyFrame);
    }
}
