// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what AgentConditionDigestService does when seeding the "last digest date" watermark on a fresh installation fails.
/// The seed commits through IUnitOfWork.CompleteAsync, which turns EF Core's DbUpdateException into the Domain
/// DatabaseUpdateException. A concurrent seed of the same key (unique violation, IsDuplicate) is not an error: the row
/// another instance stored is re-read and the run continues with its value. Any other seed failure fails the run, and
/// nothing is claimed or dispatched.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class AgentConditionDigestSeedFailureTests
{
    private const string WatermarkKey = Klacks.Api.Application.Constants.Settings.AGENT_CONDITION_DIGEST_LAST_RUN_DATE;
    private const string TodayKey = "2026-08-24";

    private static readonly DateTime PastTargetUtc = new(2026, 8, 24, 6, 31, 0, DateTimeKind.Utc);

    private ISettingsRepository _settingsRepository = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private IAgentTriggerService _triggerService = null!;
    private AgentConditionDigestService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _triggerService = Substitute.For<IAgentTriggerService>();
    }

    [Test]
    public async Task RunIfDue_AnotherInstanceSeededTheWatermarkConcurrently_TheRowIsReReadAndTheRunContinues()
    {
        GivenSeedFails(new DatabaseUpdateException("duplicate key value", isDuplicate: true), seededByAnotherInstance: TodayKey);

        var result = await _service.RunIfDueAsync();

        result.Outcome.ShouldBe(AgentConditionDigestOutcome.AlreadyRanToday);
        await AssertNothingClaimedOrDispatched();
    }

    [Test]
    public async Task RunIfDue_SeedingTheWatermarkFailsForAnotherReason_TheRunFailsWithTheUnitOfWorkException()
    {
        GivenSeedFails(new DatabaseUpdateException("database error"), seededByAnotherInstance: string.Empty);

        await Should.ThrowAsync<DatabaseUpdateException>(() => _service.RunIfDueAsync());

        await AssertNothingClaimedOrDispatched();
    }

    private void GivenSeedFails(DatabaseUpdateException failure, string seededByAnotherInstance)
    {
        _settingsRepository.GetSettingNoTracking(WatermarkKey).Returns(
            (SettingsEntity?)null,
            new SettingsEntity { Id = Guid.NewGuid(), Type = WatermarkKey, Value = seededByAnotherInstance });
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().ThrowsAsync(failure);

        _service = new AgentConditionDigestService(
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionScopeResolver>(),
            _planningAudienceResolver,
            _triggerService,
            _settingsRepository,
            unitOfWork,
            Options.Create(new BackgroundServiceOptions()),
            new FixedCompanyClock(PastTargetUtc, TimeZoneInfo.Utc),
            Substitute.For<ILogger<AgentConditionDigestService>>());
    }

    private async Task AssertNothingClaimedOrDispatched()
    {
        await _settingsRepository.DidNotReceive().TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _planningAudienceResolver.DidNotReceive().GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
        await _triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }
}
