// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of which rows each step of the single check sees, written against the unsplit service: the
/// stored timeline and the travel pairing only see the checked client's blocks of the edited day, the rest check
/// sees the window around it (reporting from the day before), and the qualification step only the client's own
/// works - not the colleague's work the client replaces on.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class ScheduleTimelineSingleCheckScopeCharacterizationTests
{
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);
    private static readonly DateOnly Wednesday = Monday.AddDays(2);

    private ScheduleTimelineTestHost _host = null!;
    private Guid _employeeId;
    private Guid _colleagueId;

    [SetUp]
    public async Task SetUp()
    {
        _host = new ScheduleTimelineTestHost();
        _employeeId = await _host.AddEmployeeAsync("Muster", "Anna");
        _colleagueId = await _host.AddEmployeeAsync("Beispiel", "Ben");
        var zurich = await _host.AddCustomerShiftAsync("Bahnhofstrasse 1", "8001", "Zürich");
        var bern = await _host.AddCustomerShiftAsync("Bundesplatz 3", "3003", "Bern");

        await _host.AddWorkAsync(_employeeId, zurich, Monday, new TimeOnly(15, 0), new TimeOnly(22, 0));
        await _host.AddWorkAsync(_employeeId, zurich, Tuesday, new TimeOnly(7, 0), new TimeOnly(12, 0));
        await _host.AddWorkAsync(_employeeId, zurich, Wednesday, new TimeOnly(6, 0), new TimeOnly(8, 0));
        await _host.AddWorkAsync(_employeeId, bern, Wednesday, new TimeOnly(8, 15), new TimeOnly(10, 0));
        var colleagueWork = await _host.AddWorkAsync(_colleagueId, bern, Tuesday, new TimeOnly(13, 0), new TimeOnly(16, 0));
        await AddReplacementAsync(colleagueWork.Id);

        _host.Travel.IsApiKeyConfiguredAsync().Returns(true);
        _host.Travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(30));
    }

    [TearDown]
    public async Task TearDown() => await _host.DisposeAsync();

    [Test]
    public async Task SingleCheck_StoresAndPairsOnlyTheEditedDayWhileTheRestCheckSeesTheDayBefore()
    {
        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        var validations = await _host.WaitForValidationsAsync(1);

        _host.Store.Received(1).SetTimeline(
            _employeeId,
            Arg.Is<ClientTimeline>(t => t.Blocks.Count > 0
                && t.Blocks.All(b => b.ClientId == _employeeId && b.OwnerDate == Tuesday)
                && t.Blocks.Any(b => b.BlockType == ScheduleBlockType.Replacement)));
        await _host.Travel.DidNotReceiveWithAnyArgs().CalculateTravelTimeAsync(default!, default!, default);

        var entries = validations.ShouldHaveSingleItem().Entries;
        entries.ShouldNotContain(e => e.Comment == ScheduleValidationKeys.TravelTimeError);
        var rest = entries.First();
        rest.Comment.ShouldBe(ScheduleValidationKeys.RestViolation);
        rest.Date.ShouldBe(Monday);
        rest.ClientId.ShouldBe(_employeeId);
    }

    [Test]
    public async Task SingleCheck_ChecksQualificationsOnlyForTheClientsOwnWorks()
    {
        _host.Service.QueueCheck(_employeeId, Tuesday, null);
        await _host.StartAsync();
        await _host.WaitForValidationsAsync(1);

        await _host.Eligibility.Received(1).BuildAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { _employeeId })),
            Arg.Is<IReadOnlyCollection<EligibilitySlot>>(slots => slots.Count == 1 && slots.All(s => s.Date == Tuesday)),
            Arg.Any<IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)>?>(),
            Arg.Any<CancellationToken>());
    }

    private async Task AddReplacementAsync(Guid workId)
    {
        await using var context = _host.NewContext();
        context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = WorkChangeType.ReplacementEnd,
            ChangeTime = 2m,
            ReplaceClientId = _employeeId,
        });
        await context.SaveChangesAsync();
    }
}
