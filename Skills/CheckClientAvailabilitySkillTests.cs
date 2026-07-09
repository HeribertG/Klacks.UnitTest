// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for check_client_availability — verifies the layered verdict: booked absence wins,
/// schedule keyword wins over availability, positive-day availability restricts to marked hours,
/// open days are deployable, and validation errors are reported without data access side effects.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CheckClientAvailabilitySkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientAvailabilityRepository _availabilityRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IScheduleCommandRepository _scheduleCommandRepository = null!;
    private CheckClientAvailabilitySkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 8, 3);

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _availabilityRepository = Substitute.For<IClientAvailabilityRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _scheduleCommandRepository = Substitute.For<IScheduleCommandRepository>();

        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Anna", Name = "Muster" });
        _availabilityRepository.GetByClientAndDateRange(ClientId, Date, Date)
            .Returns(Task.FromResult(new List<ClientAvailability>()));
        _breakRepository.GetByClientAndDateRangeAsync(ClientId, Date, Date, Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        _scheduleCommandRepository
            .GetByClientsAndDateRangeAsync(Arg.Any<IReadOnlyList<Guid>>(), Date, Date, null, Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand>());

        _skill = new CheckClientAvailabilitySkill(
            _clientRepository, _availabilityRepository, _breakRepository, _scheduleCommandRepository);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(int? startHour = null, int? endHour = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-08-03"
        };
        if (startHour.HasValue)
        {
            parameters["startHour"] = startHour.Value;
        }

        if (endHour.HasValue)
        {
            parameters["endHour"] = endHour.Value;
        }

        return parameters;
    }

    private void SetAvailability(params (int Hour, bool Available)[] slots)
    {
        _availabilityRepository.GetByClientAndDateRange(ClientId, Date, Date)
            .Returns(Task.FromResult(slots.Select(s => new ClientAvailability
            {
                Id = Guid.NewGuid(),
                ClientId = ClientId,
                Date = Date,
                Hour = s.Hour,
                IsAvailable = s.Available
            }).ToList()));
    }

    [Test]
    public async Task OpenDay_IsDeployable()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("open for planning");
    }

    [Test]
    public async Task BookedAbsence_WinsOverEverything()
    {
        SetAvailability((8, true));
        _breakRepository.GetByClientAndDateRangeAsync(ClientId, Date, Date, Arg.Any<CancellationToken>())
            .Returns(new List<Break> { new() { Id = Guid.NewGuid(), ClientId = ClientId, CurrentDate = Date } });

        var result = await _skill.ExecuteAsync(Context(), Parameters(8, 8));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("booked absence");
        result.Message.ShouldContain("not deployable");
    }

    [Test]
    public async Task Keyword_WinsOverAvailability()
    {
        SetAvailability((8, true));
        _scheduleCommandRepository
            .GetByClientsAndDateRangeAsync(Arg.Any<IReadOnlyList<Guid>>(), Date, Date, null, Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleCommand>
            {
                new() { ClientId = ClientId, CurrentDate = Date, CommandKeyword = "FREE" }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters(8, 8));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("FREE");
        result.Message.ShouldContain("override");
    }

    [Test]
    public async Task PositiveDay_WindowInsideMarkedHours_IsDeployable()
    {
        SetAvailability((8, true), (9, true), (10, true), (11, true));

        var result = await _skill.ExecuteAsync(Context(), Parameters(9, 10));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("deployable");
        result.Message.ShouldNotContain("NOT deployable");
    }

    [Test]
    public async Task PositiveDay_WindowOutsideMarkedHours_IsNotDeployable()
    {
        SetAvailability((8, true), (9, true));

        var result = await _skill.ExecuteAsync(Context(), Parameters(14, 16));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("NOT deployable");
        result.Message.ShouldContain("positively marked");
    }

    [Test]
    public async Task LegacyNegativeDay_BlockedHourInWindow_IsNotDeployable()
    {
        SetAvailability((14, false), (15, false));

        var result = await _skill.ExecuteAsync(Context(), Parameters(14, 16));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("NOT deployable");
        result.Message.ShouldContain("explicitly blocked");
    }

    [Test]
    public async Task InvalidHourWindow_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(20, 5));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("hour");
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Get(ClientId).Returns(Task.FromResult<Client?>(null));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }
}
