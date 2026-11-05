// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for set_client_availability: a date range expands to one slot per day and hour
/// (full day by default), explicit single dates with an hour window build only those slots,
/// the write is verified by re-reading the persisted slots (honest warning on mismatch),
/// and invalid hour windows or oversized ranges are rejected without touching the mediator.
/// </summary>

using Klacks.Api.Application.Commands.ClientAvailabilities;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetClientAvailabilitySkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IClientAvailabilityRepository _availabilityRepository = null!;
    private IClientRepository _clientRepository = null!;
    private SetClientAvailabilitySkill _skill = null!;
    private ClientAvailabilityBulkRequest? _captured;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _availabilityRepository = Substitute.For<IClientAvailabilityRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.Exists(ClientId).Returns(true);
        _captured = null;

        _mediator.Send(Arg.Do<BulkUpdateClientAvailabilityCommand>(c => _captured = c.Request), Arg.Any<CancellationToken>())
            .Returns(ci => ((BulkUpdateClientAvailabilityCommand)ci[0]).Request.Items.Count);

        _availabilityRepository.GetByClientAndDateRange(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(ci => Task.FromResult(
                (_captured?.Items ?? []).Select(i => new ClientAvailability
                {
                    Id = Guid.NewGuid(),
                    ClientId = i.ClientId,
                    Date = i.Date,
                    Hour = i.Hour,
                    IsAvailable = i.IsAvailable!.Value
                }).ToList()));

        _skill = NewSkill(Substitute.For<IClientSearchRepository>());
    }

    private SetClientAvailabilitySkill NewSkill(IClientSearchRepository search) =>
        new(_mediator, _availabilityRepository, _clientRepository, search, SkillClientVisibility.AllVisible());

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task SetRange_DefaultHours_BuildsFullDaySlotsForEachDay()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-06-15",
            ["endDate"] = "2026-06-16",
            ["isAvailable"] = false
        });

        result.Success.ShouldBeTrue();
        _captured.ShouldNotBeNull();
        _captured!.Items.Count.ShouldBe(48);
        _captured.Items.ShouldAllBe(i => i.ClientId == ClientId && i.IsAvailable == false);
        _captured.Items.Count(i => i.Date == new DateOnly(2026, 6, 15)).ShouldBe(24);
        _captured.Items.Count(i => i.Date == new DateOnly(2026, 6, 16)).ShouldBe(24);
        _captured.Items.Min(i => i.Hour).ShouldBe(0);
        _captured.Items.Max(i => i.Hour).ShouldBe(23);
        result.Message.ShouldContain("unavailable");
        result.Message.ShouldContain("verified");
    }

    [Test]
    public async Task SetSingleDates_HourWindow_BuildsOnlyRequestedSlots()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["dates"] = "2026-06-15, 2026-06-17",
            ["startHour"] = 8,
            ["endHour"] = 10,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeTrue();
        _captured.ShouldNotBeNull();
        _captured!.Items.Count.ShouldBe(6);
        _captured.Items.ShouldAllBe(i => i.IsAvailable == true && i.Hour >= 8 && i.Hour <= 10);
        _captured.Items.Select(i => i.Date).Distinct().Count().ShouldBe(2);
        result.Message.ShouldContain("available");
        result.Message.ShouldContain("verified");
    }

    [Test]
    public async Task SetAvailability_PersistMismatch_ReportsHonestWarning()
    {
        _availabilityRepository.GetByClientAndDateRange(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(Task.FromResult(new List<ClientAvailability>()));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-06-15",
            ["startHour"] = 8,
            ["endHour"] = 10,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("WARNING");
        result.Message.ShouldContain("0 of 3");
    }

    [Test]
    public async Task SetAvailability_InvalidHourWindow_ReturnsError_NoMutation()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-06-15",
            ["startHour"] = 20,
            ["endHour"] = 5,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("hour");
        await _mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAvailability_RangeTooLarge_ReturnsError_NoMutation()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-12-31",
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("range");
        await _mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAvailability_InvalidDateInList_ReturnsError_NoMutation()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["dates"] = "2026-06-15, not-a-date",
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not-a-date");
        await _mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NameInsteadOfClientId_ResolvesThePerson_AndWritesTheHourWindowForThatClient()
    {
        var miaId = Guid.NewGuid();
        var search = SkillClientNameSearch.Finding(SkillClientNameSearch.Row(miaId, "Mia", "Baer", 5041));
        SkillClientNameSearch.Loads(_clientRepository, miaId, "Mia", "Baer", 5041);
        _availabilityRepository.GetByClientAndDateRange(miaId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(ci => Task.FromResult(
                (_captured?.Items ?? []).Select(i => new ClientAvailability
                {
                    Id = Guid.NewGuid(), ClientId = i.ClientId, Date = i.Date, Hour = i.Hour, IsAvailable = i.IsAvailable!.Value
                }).ToList()));

        var result = await NewSkill(search).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Mia",
            ["lastName"] = "Baer",
            ["dates"] = "2026-12-07,2026-12-08,2026-12-09",
            ["startHour"] = 7,
            ["endHour"] = 14,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeTrue(result.Message);
        _captured.ShouldNotBeNull();
        _captured!.Items.Count.ShouldBe(24);
        _captured.Items.ShouldAllBe(i => i.ClientId == miaId && i.IsAvailable == true && i.Hour >= 7 && i.Hour <= 14);
    }

    [Test]
    public async Task ClientNumberPassedAsClientId_IsRefusedWithTheIdNumberHint_WithoutWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = "5044",
            ["startDate"] = "2026-12-21",
            ["isAvailable"] = false
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("idNumber");
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(BulkUpdateClientAvailabilityCommand)!, default);
    }

    [Test]
    public async Task UnknownClientId_IsRefused_WithoutWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["startDate"] = "2026-12-21",
            ["isAvailable"] = false
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(BulkUpdateClientAvailabilityCommand)!, default);
    }

    [Test]
    public async Task AmbiguousName_AsksForTheClientNumber_WithoutWrite()
    {
        var search = SkillClientNameSearch.Finding(
            SkillClientNameSearch.Row(Guid.NewGuid(), "Priya", "Nair", 7),
            SkillClientNameSearch.Row(Guid.NewGuid(), "Priya", "Nair", 9));

        var result = await NewSkill(search).ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Priya",
            ["lastName"] = "Nair",
            ["startDate"] = "2026-12-21",
            ["isAvailable"] = false
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("#7");
        result.Message.ShouldContain("#9");
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(BulkUpdateClientAvailabilityCommand)!, default);
    }
}
