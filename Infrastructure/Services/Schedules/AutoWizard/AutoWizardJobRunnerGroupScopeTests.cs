// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every stage of the AutoWizard chain plans for the group of the start request: Wizard 1, the Harmonizer and the Holistic
/// Harmonizer receive its GroupId, so all three close the days outside an employee's period in that group. Without it the
/// planner placed temporary workers outside their group period (the apply then dropped them and left holes) and the
/// harmonizers moved regular duties onto them on such days, unfiltered. The report contexts of the run (remaining rule
/// violations, qualification gaps) read the same group.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Constants;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobRunnerGroupScopeTests : AutoWizardJobRunnerTestHarness
{
    [Test]
    public async Task EveryStage_ReceivesTheGroupOfTheRequest()
    {
        var groupId = Guid.NewGuid();

        var (_, state) = await RunChainAsync(Request(groupId));

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        await _wizardRunner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.GroupId == groupId), Arg.Any<CancellationToken>());
        await _harmonizerRunner.Received(1).StartAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.GroupId == groupId), Arg.Any<CancellationToken>());
        await _holisticRunner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.GroupId == groupId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheReportContexts_ReadTheSameGroupAsTheStages()
    {
        var groupId = Guid.NewGuid();

        var (_, state) = await RunChainAsync(Request(groupId));

        state.Status.ShouldBe(WizardJobStatusValues.Completed);
        var reportRequests = _contextBuilder.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<HarmonizerContextRequest>()
            .ToList();
        reportRequests.ShouldNotBeEmpty("the run counts the remaining rule violations and the qualification gaps on a harmonizer context");
        reportRequests.ShouldAllBe(r => r.GroupId == groupId);
    }
}
