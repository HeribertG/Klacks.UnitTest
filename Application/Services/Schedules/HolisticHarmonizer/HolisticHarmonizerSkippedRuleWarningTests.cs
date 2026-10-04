// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// An invalid approved hard planning constraint must not stop stage 3: the context carries it as skipped, the run
/// completes and keeps honouring the valid hard rules, and the response warns about the skipped rule with the
/// translated planning-rule-invalid key.
/// </summary>
[TestFixture]
public class HolisticHarmonizerSkippedRuleWarningTests
{
    [Test]
    public async Task InvalidPlusValidHardRule_RunCompletes_HonoursTheValidRule_AndWarns()
    {
        var invalidRuleId = Guid.NewGuid();
        var fixture = PlanningRuleBitmapFixture.Build(2, PlanningRuleBitmapFixture.HardRules());
        var input = fixture with { Rules = fixture.Rules! with { InvalidHardRuleIds = [invalidRuleId] } };
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder.BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>()).Returns(input);
        var engine = new HolisticHarmonizerDeterministicEngine(contextBuilder, NullLogger<HolisticHarmonizerDeterministicEngine>.Instance);
        var context = PlanningRuleBitmapFixture.Context(input);
        var oracle = PlanRuleEvaluatorFactory.Create(input.Rules!.Rules, context);

        var result = await engine.RunAsync(
            new HolisticHarmonizerRunInput(input.StartDate, input.EndDate, input.Agents.Select(_ => Guid.NewGuid()).ToList(), null, "de"),
            progress: null,
            CancellationToken.None);
        var response = HolisticHarmonizerResponseMapper.ToResponse(Guid.NewGuid(), result);

        result.InvalidPlanningRuleIds.ShouldBe([invalidRuleId]);
        var before = PlanningRuleBitmapFixture.HardExcess(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, result.OriginalBitmap)));
        var after = PlanningRuleBitmapFixture.HardExcess(oracle.Evaluate(PlanningRuleBitmapFixture.OraclePlan(context, input, result.FinalBitmap)));
        foreach (var (key, excess) in after)
        {
            excess.ShouldBeLessThanOrEqualTo(before.GetValueOrDefault(key));
        }

        var warning = response.PlanningRuleWarnings!.ShouldHaveSingleItem();
        warning.Comment.ShouldBe(ScheduleValidationKeys.PlanningRuleInvalid);
        warning.Type.ShouldBe(ScheduleValidationType.Warning);
        warning.CommentParams![PlanningRuleNotificationMapper.RuleIdParam].ShouldBe(invalidRuleId.ToString());
        warning.Date.ShouldBe(input.StartDate);
    }

    [Test]
    public void ToResponse_WithoutSkippedRules_HasNoWarning()
    {
        var bitmap = new HarmonyBitmap([], [], new Cell[0, 0]);
        var result = new Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations.HolisticHarmonizerRunResult(bitmap, bitmap, [], 0, 0, "x", null, null);

        HolisticHarmonizerResponseMapper.ToResponse(Guid.NewGuid(), result).PlanningRuleWarnings!.ShouldBeEmpty();
    }
}
