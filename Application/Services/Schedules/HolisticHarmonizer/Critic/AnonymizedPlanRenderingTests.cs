// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer.Critic;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer.Critic;

/// <summary>
/// Nothing that identifies an employee may reach the critic model: rows are r00, r01, ... in both the image and the
/// text grid, and the text grid gives the day index the critic has to cite.
/// </summary>
[TestFixture]
public sealed class AnonymizedPlanRenderingTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    [Test]
    public void TextGrid_UsesRowIndicesInsteadOfNames()
    {
        var text = AnonymizedPlanTextRenderer.Render(PlanCriticTestPlans.TwoAgentsThreeDays());

        text.ShouldContain("r00");
        text.ShouldContain("r01");
        text.ShouldNotContain(PlanCriticTestPlans.FirstAgentName);
        text.ShouldNotContain("Anna");
        text.ShouldNotContain("Beat");
    }

    [Test]
    public void TextGrid_ShowsDayIndexSymbolsAndLocks()
    {
        var lines = AnonymizedPlanTextRenderer.Render(PlanCriticTestPlans.TwoAgentsThreeDays())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd())
            .ToArray();

        lines[0].Trim().ShouldBe("d00  d01  d02");
        lines[1].ShouldContain("24Sa");
        lines[2].ShouldBe("r00   N    E*   _");
        lines[3].ShouldBe("r01   _    _    B*");
    }

    [Test]
    public void ImageRowLabels_AreRowIndices()
    {
        AnonymizedPlanImageRenderer.RowLabels(PlanCriticTestPlans.TwoAgentsThreeDays())
            .ShouldBe(new[] { "r00", "r01" });
    }

    [Test]
    public void Image_IsAPng()
    {
        var png = new AnonymizedPlanImageRenderer().Render(PlanCriticTestPlans.TwoAgentsThreeDays());

        png.Take(PngSignature.Length).ShouldBe(PngSignature);
    }
}
