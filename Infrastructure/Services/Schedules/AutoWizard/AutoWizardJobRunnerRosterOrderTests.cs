// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The roster order the planner arranged by hand holds in every stage of the AutoWizard chain: Wizard 1, the Harmonizer
/// and the Holistic Harmonizer receive AgentOrderIsUserDefined from the start request. Before, only stage 1 got it and
/// stages 2 and 3 re-sorted the rows by their own key.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobRunnerRosterOrderTests : AutoWizardJobRunnerTestHarness
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task EveryStage_ReceivesTheUserDefinedOrderFlag(bool userDefined)
    {
        var request = Request(Guid.NewGuid()) with { AgentOrderIsUserDefined = userDefined };

        var (_, state) = await RunChainAsync(request);

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.AgentOrderIsUserDefined == userDefined), Arg.Any<CancellationToken>());
        await _harmonizerRunner.Received(1).StartAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.AgentOrderIsUserDefined == userDefined), Arg.Any<CancellationToken>());
        await _holisticRunner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.AgentOrderIsUserDefined == userDefined), Arg.Any<CancellationToken>());
    }
}
