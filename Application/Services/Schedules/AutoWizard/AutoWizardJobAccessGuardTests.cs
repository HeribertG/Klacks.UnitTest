// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the single job access rule of the normal autofill (owner rule 2026-11-07: autofill is planning work, open to every
/// planner): an unrestricted caller addresses every job, a restricted caller only an AutoWizard job whose recorded planning
/// group and every recorded employee are visible to them; an unknown, expired, group-less or other-family job, or one that
/// plans a hidden employee, is refused, so status, cancel and the hub answer it like a missing job and no employee names
/// leave the caller's scope.
/// </summary>

using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Schedules.AutoWizard;

[TestFixture]
public class AutoWizardJobAccessGuardTests
{
    private static readonly Guid HiddenAgentId = Guid.NewGuid();

    private IGroupVisibilityGuard _groupGuard = null!;
    private SettableTimeProvider _time = null!;
    private AutoWizardJobScopes _scopes = null!;
    private AutoWizardJobAccessGuard _guard = null!;

    [SetUp]
    public void Setup()
    {
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _time = new SettableTimeProvider(new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero));
        _scopes = new AutoWizardJobScopes(_time);
        _guard = new AutoWizardJobAccessGuard(_groupGuard, TestGroupWriteVisibility.ClientsHidden(HiddenAgentId), _scopes);
    }

    [Test]
    public async Task UnrestrictedCaller_AddressesEveryJob_EvenAnUnrecordedOne()
    {
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        (await _guard.CanAccessAsync(Guid.NewGuid())).ShouldBeTrue();
    }

    [Test]
    public async Task RestrictedCaller_AddressesAJobOfAVisibleGroup()
    {
        var jobId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _scopes.Record(jobId, groupId, [Guid.NewGuid()]);
        _groupGuard.IsGroupVisibleAsync(groupId, Arg.Any<CancellationToken>()).Returns(true);

        (await _guard.CanAccessAsync(jobId)).ShouldBeTrue();
    }

    [Test]
    public async Task RestrictedCaller_IsRefusedAJobOfAVisibleGroupThatPlansAHiddenEmployee()
    {
        var jobId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _scopes.Record(jobId, groupId, [Guid.NewGuid(), HiddenAgentId]);
        _groupGuard.IsGroupVisibleAsync(groupId, Arg.Any<CancellationToken>()).Returns(true);

        (await _guard.CanAccessAsync(jobId)).ShouldBeFalse("the job's results name the employees it planned");
    }

    [Test]
    public async Task RestrictedCaller_IsRefusedAJobOfAHiddenGroup()
    {
        var jobId = Guid.NewGuid();
        var hiddenGroupId = Guid.NewGuid();
        _scopes.Record(jobId, hiddenGroupId, []);
        _groupGuard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);

        (await _guard.CanAccessAsync(jobId)).ShouldBeFalse();
    }

    [Test]
    public async Task RestrictedCaller_IsRefusedAGrouplessJob()
    {
        var jobId = Guid.NewGuid();
        _scopes.Record(jobId, null, []);
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        (await _guard.CanAccessAsync(jobId)).ShouldBeFalse();
    }

    [Test]
    public async Task RestrictedCaller_IsRefusedAJobThatWasNeverRecorded()
    {
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        (await _guard.CanAccessAsync(Guid.NewGuid())).ShouldBeFalse("jobs of the developer-dropdown wizards are never recorded");
    }

    [Test]
    public void Scopes_DropAnEntryOnceItsRunAndItsTerminalStateHaveExpired()
    {
        var oldJob = Guid.NewGuid();
        var keptJob = Guid.NewGuid();
        _scopes.Record(oldJob, Guid.NewGuid(), []);
        _time.Now = _time.Now.AddMinutes(AutoWizardTimeBudget.TotalMinutes + JobTerminalStateDefaults.TtlMinutes - 1);
        _scopes.Record(keptJob, Guid.NewGuid(), []);
        _scopes.Find(oldJob).ShouldNotBeNull("still inside the retention window");

        _time.Now = _time.Now.AddMinutes(2);
        _scopes.Record(Guid.NewGuid(), Guid.NewGuid(), []);

        _scopes.Find(oldJob).ShouldBeNull();
        _scopes.Find(keptJob).ShouldNotBeNull();
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
