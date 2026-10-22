// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what AgentConditionDigestService does when seeding the "last digest date" watermark on a fresh installation fails.
/// The seed commits through IUnitOfWork.CompleteAsync, which turns EF Core's DbUpdateException into the Domain
/// DatabaseUpdateException (a concurrent seed of the same key = unique violation, IsDuplicate); that is not a
/// DbUpdateException, so the run fails with it instead of re-reading the row another instance seeded, and nothing is
/// claimed or dispatched.
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

    private static readonly DateTime PastTargetUtc = new(2026, 8, 24, 6, 31, 0, DateTimeKind.Utc);

    [Test]
    public async Task RunIfDue_SeedingTheWatermarkFails_TheRunFailsWithTheUnitOfWorkException()
    {
        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetSettingNoTracking(WatermarkKey).Returns(
            (SettingsEntity?)null,
            new SettingsEntity { Id = Guid.NewGuid(), Type = WatermarkKey, Value = string.Empty });
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().ThrowsAsync(new DatabaseUpdateException("duplicate key value", isDuplicate: true));
        var planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        var triggerService = Substitute.For<IAgentTriggerService>();

        var service = new AgentConditionDigestService(
            Substitute.For<IAgentConditionRepository>(),
            Substitute.For<IAgentConditionScopeResolver>(),
            planningAudienceResolver,
            triggerService,
            settingsRepository,
            unitOfWork,
            Options.Create(new BackgroundServiceOptions()),
            new FixedCompanyClock(PastTargetUtc, TimeZoneInfo.Utc),
            Substitute.For<ILogger<AgentConditionDigestService>>());

        var thrown = await Should.ThrowAsync<DatabaseUpdateException>(() => service.RunIfDueAsync());

        thrown.IsDuplicate.ShouldBeTrue();
        await settingsRepository.DidNotReceive().TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await planningAudienceResolver.DidNotReceive().GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
        await triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
    }
}
