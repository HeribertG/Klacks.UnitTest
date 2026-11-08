// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A group-restricted planner may start the normal autofill (owner rule 2026-11-07), but only on shifts they may see:
/// the chain runs without a user and an empty shift selection means every shift of the installation, so the start
/// refuses a missing selection (400) and a hidden shift like a missing one (404) before any job exists.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.AutoWizard;

[TestFixture]
public sealed class AutoWizardJobRunnerRestrictedStartTests : AutoWizardJobRunnerTestHarness
{
    private static readonly Guid HiddenShiftId = Guid.NewGuid();

    protected override void AddServices(ServiceCollection services)
    {
        var restricted = Substitute.For<IGroupVisibilityGuard>();
        restricted.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        restricted.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        services.AddSingleton(restricted);
        services.AddSingleton(ShiftVisibilityGuards.Hiding(HiddenShiftId));
    }

    [Test]
    public async Task HiddenShift_IsRefusedLikeAMissingOne()
    {
        var request = Request(Guid.NewGuid()) with { ShiftIds = [Guid.NewGuid(), HiddenShiftId] };

        await Should.ThrowAsync<KeyNotFoundException>(() => _runner.StartAsync(request, CancellationToken.None));
        await _wizardRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task MissingShiftSelection_IsRefused()
    {
        var request = Request(Guid.NewGuid()) with { ShiftIds = null };

        await Should.ThrowAsync<BadRequestException>(() => _runner.StartAsync(request, CancellationToken.None));
    }

    [Test]
    public async Task VisibleShifts_Start()
    {
        var (_, state) = await RunChainAsync(Request(Guid.NewGuid()));

        state.Found.ShouldBeTrue();
    }
}
