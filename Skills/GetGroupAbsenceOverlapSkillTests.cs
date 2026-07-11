// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetGroupAbsenceOverlapSkill — verifies group resolution failure, the maximum
/// range guard, correct separate counting of booked (Break) and planned (BreakPlaceholder)
/// absences per day, and peak-day detection across a group's members.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using BreakFilter = Klacks.Api.Domain.Models.Filters.BreakFilter;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetGroupAbsenceOverlapSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IClientBreakPlaceholderRepository _clientBreakPlaceholderRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private GetGroupAbsenceOverlapSkill _skill = null!;

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid VacationAbsenceId = Guid.NewGuid();
    private static readonly Guid SickAbsenceId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _clientBreakPlaceholderRepository = Substitute.For<IClientBreakPlaceholderRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = GroupId, Name = "Sales" }
        });

        _absenceRepository.List().Returns(new List<Absence>
        {
            new() { Id = VacationAbsenceId, Name = new MultiLanguage { De = "Ferien", En = "Vacation" } },
            new() { Id = SickAbsenceId, Name = new MultiLanguage { De = "Krank", En = "Sick" } }
        });

        _clientBreakPlaceholderRepository
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<Client>(), 0));

        _skill = new GetGroupAbsenceOverlapSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _clientBreakPlaceholderRepository, _absenceRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(
        string groupName = "Sales", string from = "2026-08-01", string until = "2026-08-10") => new()
    {
        ["groupName"] = groupName,
        ["fromDate"] = from,
        ["untilDate"] = until
    };

    private static Client BuildClient(string firstName, string lastName) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = firstName,
        Name = lastName
    };

    [Test]
    public async Task UnknownGroup_ReturnsError_ListingRealGroups()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(groupName: "Nonexistent"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Sales");
        await _clientBreakPlaceholderRepository.DidNotReceive()
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(from: "2026-08-10", until: "2026-08-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be before");
    }

    [Test]
    public async Task RangeExceedsMaximum_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(
            Ctx(), Parameters(from: "2026-01-01", until: "2027-06-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("exceeds the maximum");
        await _clientBreakPlaceholderRepository.DidNotReceive()
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoMembers_ReturnsZeroGroupSize_AndNoDays()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = (GetGroupAbsenceOverlapResult)result.Data!;
        data.GroupSize.ShouldBe(0);
        data.Days.Count.ShouldBe(0);
        data.PeakCount.ShouldBe(0);
    }

    [Test]
    public async Task CountsBookedAndPlannedAbsences_Separately()
    {
        var bookedClient = BuildClient("Anna", "Muster");
        bookedClient.Breaks = new List<Break>
        {
            new() { Id = Guid.NewGuid(), ClientId = bookedClient.Id, AbsenceId = SickAbsenceId, CurrentDate = new DateOnly(2026, 8, 5) }
        };

        var plannedClient = BuildClient("Max", "Beispiel");
        plannedClient.BreakPlaceholders = new List<BreakPlaceholder>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ClientId = plannedClient.Id,
                AbsenceId = VacationAbsenceId,
                From = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
                Until = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var otherClient = BuildClient("Eva", "Ohnegrund");

        _clientBreakPlaceholderRepository
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<Client> { bookedClient, plannedClient, otherClient }, 3));

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = (GetGroupAbsenceOverlapResult)result.Data!;
        data.GroupSize.ShouldBe(3);

        var day5 = data.Days.Single(d => d.Date == new DateOnly(2026, 8, 5));
        day5.BookedCount.ShouldBe(1);
        day5.PlannedCount.ShouldBe(1);
        day5.TotalAbsentCount.ShouldBe(2);
        day5.BookedMembers.Single().ClientId.ShouldBe(bookedClient.Id);
        day5.PlannedMembers.Single().ClientId.ShouldBe(plannedClient.Id);

        var day4 = data.Days.Single(d => d.Date == new DateOnly(2026, 8, 4));
        day4.BookedCount.ShouldBe(0);
        day4.PlannedCount.ShouldBe(1);
        day4.TotalAbsentCount.ShouldBe(1);
    }

    [Test]
    public async Task OverlappingPlaceholdersForSameClient_CountOnce()
    {
        var client = BuildClient("Anna", "Doppelt");
        client.BreakPlaceholders = new List<BreakPlaceholder>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                AbsenceId = VacationAbsenceId,
                From = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                Until = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)
            },
            new()
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                AbsenceId = SickAbsenceId,
                From = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
                Until = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        _clientBreakPlaceholderRepository
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<Client> { client }, 1));

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = (GetGroupAbsenceOverlapResult)result.Data!;

        var overlapDay = data.Days.Single(d => d.Date == new DateOnly(2026, 8, 5));
        overlapDay.PlannedCount.ShouldBe(1);
        overlapDay.PlannedMembers.Count.ShouldBe(1);
        overlapDay.TotalAbsentCount.ShouldBe(1);
    }

    [Test]
    public async Task PeakDay_IsHighestSimultaneousOverlap()
    {
        var client1 = BuildClient("Anna", "Eins");
        var client2 = BuildClient("Beat", "Zwei");
        var client3 = BuildClient("Carla", "Drei");

        var peakDay = new DateOnly(2026, 8, 5);

        client1.Breaks = new List<Break>
        {
            new() { Id = Guid.NewGuid(), ClientId = client1.Id, AbsenceId = SickAbsenceId, CurrentDate = peakDay }
        };
        client2.Breaks = new List<Break>
        {
            new() { Id = Guid.NewGuid(), ClientId = client2.Id, AbsenceId = SickAbsenceId, CurrentDate = peakDay }
        };
        client3.Breaks = new List<Break>
        {
            new() { Id = Guid.NewGuid(), ClientId = client3.Id, AbsenceId = SickAbsenceId, CurrentDate = new DateOnly(2026, 8, 2) }
        };

        _clientBreakPlaceholderRepository
            .BreakList(Arg.Any<BreakFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<Client> { client1, client2, client3 }, 3));

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        var data = (GetGroupAbsenceOverlapResult)result.Data!;
        data.PeakCount.ShouldBe(2);
        data.PeakDates.ShouldContain(peakDay);
        result.Message.ShouldContain("2 of 3");
        result.Message.ShouldContain(peakDay.ToString("yyyy-MM-dd"));
    }
}
