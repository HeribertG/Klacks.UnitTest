// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the date and time arguments an LLM hands to a skill (and the ISO strings of the planning scope and
/// the grounding pool) are read the same way whatever culture the server process runs with. The fixture switches
/// CurrentCulture to ar-SA (Umm al-Qura calendar, rejects "2026-10-23"), th-TH (Buddhist calendar, reads 2026 as
/// Gregorian 1483), de-CH and en-US and asserts identical outcomes; before the culture-sensitive Parse calls were
/// replaced these inputs were rejected or shifted under the first two.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Grounding;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class SkillCultureInvariantParsingTests
{
    private const string IsoDateText = "2026-10-23";
    private const string LaterIsoDateText = "2026-10-25";
    private const string EnglishLanguage = "en";
    private const string GermanLanguage = "de";
    private const string AfternoonClockText = "2:30 PM";
    private const string MorningClockText = "08:00";
    private const string DateErrorFragment = "ISO format";
    private const string NotAContainerFragment = "is not a container shift";
    private const string OrderErrorFragment = "must be on or after fromDate";
    private const string TimeOrderErrorFragment = "must be after fromTime";
    private const string InvalidTimeFragment = "Invalid fromTime";

    private static readonly DateTimeOffset CompanyNow = new(2026, 7, 10, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] ProcessCultures = ["ar-SA", "th-TH", "de-CH", "en-US"];

    private static SkillExecutionContext Context(string? language) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "admin",
        UserPermissions = new List<string>(),
        UserLanguage = language
    };

    [Test]
    public async Task ResetContainerDay_IsoDate_IsAccepted_InEveryProcessCulture()
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);
        var skill = new ResetContainerDaySkill(
            shiftRepository, Substitute.For<IMediator>(), Substitute.For<IShiftVisibilityGuard>());

        await InEveryProcessCultureAsync(async () =>
        {
            var result = await skill.ExecuteAsync(Context(GermanLanguage), new Dictionary<string, object>
            {
                ["containerId"] = Guid.NewGuid().ToString(),
                ["date"] = IsoDateText
            });

            result.Success.ShouldBeFalse();
            result.Message!.ShouldNotContain(DateErrorFragment);
            result.Message!.ShouldContain(NotAContainerFragment);
        });
    }

    [Test]
    public async Task DetectConflicts_IsoDates_AreBothParsed_InEveryProcessCulture()
    {
        var skill = new DetectConflictsSkill(
            Substitute.For<IPeriodValidationLoader>(),
            Substitute.For<IGroupRepository>(),
            Substitute.For<IGroupScopeGuard>(),
            Substitute.For<IClientVisibilityGuard>());

        await InEveryProcessCultureAsync(async () =>
        {
            var result = await skill.ExecuteAsync(Context(GermanLanguage), new Dictionary<string, object>
            {
                ["groupId"] = Guid.NewGuid().ToString(),
                ["fromDate"] = LaterIsoDateText,
                ["untilDate"] = IsoDateText
            });

            result.Success.ShouldBeFalse();
            result.Message!.ShouldContain(OrderErrorFragment);
        });
    }

    [TestCase(GermanLanguage, "14:30")]
    [TestCase(EnglishLanguage, AfternoonClockText)]
    [TestCase(null, "14:30")]
    public async Task CreateContainerTemplate_TimeText_IsReadByTheUserLanguage_NotByTheProcessCulture(
        string? language, string fromTime)
    {
        var skill = new CreateContainerTemplateSkill(
            Substitute.For<IShiftRepository>(),
            Substitute.For<IContainerTemplateRepository>(),
            Substitute.For<IKlacksSelfApiClient>(),
            Substitute.For<ISelfApiRouteResolver>(),
            Substitute.For<IShiftVisibilityGuard>());

        await InEveryProcessCultureAsync(async () =>
        {
            var result = await skill.ExecuteAsync(Context(language), new Dictionary<string, object>
            {
                ["containerId"] = Guid.NewGuid().ToString(),
                ["weekday"] = 1,
                ["fromTime"] = fromTime,
                ["untilTime"] = MorningClockText
            });

            result.Success.ShouldBeFalse();
            result.Message!.ShouldNotContain(InvalidTimeFragment);
            result.Message!.ShouldContain(TimeOrderErrorFragment);
        });
    }

    [Test]
    public async Task PlanningScopeEnricher_IsoPeriodFrom_IsReadAsTheWrittenDay_InEveryProcessCulture()
    {
        var ruleContext = Substitute.For<IRuleContextProvider>();
        ruleContext.IsSchedulingContext(Arg.Any<IReadOnlyList<string>>()).Returns(true);
        var resolver = Substitute.For<ISchedulingPolicyResolver>();
        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2);
        resolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
        var enricher = new PlanningScopeEnricher(ruleContext, resolver, new FixedCompanyClock(CompanyNow));
        var clientId = Guid.NewGuid();

        await InEveryProcessCultureAsync(async () =>
        {
            resolver.ClearReceivedCalls();
            var context = new LLMContext
            {
                Language = GermanLanguage,
                AvailableFunctions = new() { new LLMFunction { Name = "find_replacement" } },
                PageContext = new AssistantPageContext
                {
                    SelectedClientId = clientId.ToString(),
                    SelectedPeriodFrom = IsoDateText
                }
            };

            await enricher.EnrichAsync(context);

            await resolver.Received(1).GetForClientAsync(clientId, new DateOnly(2026, 10, 23));
        });
    }

    [Test]
    public void GroundingPool_IsoDateString_IsCovered_InEveryProcessCulture()
    {
        var call = new LLMFunctionCall
        {
            FunctionName = "some_skill",
            Success = true,
            ResultKind = LLMFunctionResultKind.Data,
            DataJson = [$$"""{"Date":"{{IsoDateText}}"}"""]
        };

        InEveryProcessCulture(() =>
        {
            var pool = ToolResultGroundingPoolBuilder.Build([call], [], GermanLanguage);

            pool.Covers(new AnswerClaim(AnswerClaimKind.Date, "23.10.2026", [IsoDateText])).ShouldBeTrue();
        });
    }

    private static void InEveryProcessCulture(Action assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var cultureName in ProcessCultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                assertion();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static async Task InEveryProcessCultureAsync(Func<Task> assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var cultureName in ProcessCultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                await assertion();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
