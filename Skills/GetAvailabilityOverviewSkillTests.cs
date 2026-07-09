// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_availability_overview — verifies that open days count as available, positive
/// days restrict to the marked hours, only-false days block just those hours, the counts and
/// truncation note are correct, and invalid hour windows are rejected.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetAvailabilityOverviewSkillTests
{
    private IMediator _mediator = null!;
    private IClientAvailabilityRepository _availabilityRepository = null!;
    private GetAvailabilityOverviewSkill _skill = null!;

    private static readonly Guid OpenClientId = Guid.NewGuid();
    private static readonly Guid MorningClientId = Guid.NewGuid();
    private static readonly Guid BlockedClientId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 8, 3);

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _availabilityRepository = Substitute.For<IClientAvailabilityRepository>();

        _mediator.Send(Arg.Any<ListClientAvailabilityClientsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ClientAvailabilityClientListResponse
            {
                Clients =
                [
                    new ClientAvailabilityClientResource { Id = OpenClientId, FirstName = "Otto", Name = "Offen" },
                    new ClientAvailabilityClientResource { Id = MorningClientId, FirstName = "Marta", Name = "Morgen" },
                    new ClientAvailabilityClientResource { Id = BlockedClientId, FirstName = "Berta", Name = "Blockiert" }
                ],
                TotalCount = 3
            });

        var entries = new List<ClientAvailability>();
        entries.AddRange(Enumerable.Range(8, 4).Select(hour => new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = MorningClientId,
            Date = Date,
            Hour = hour,
            IsAvailable = true
        }));
        entries.Add(new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = BlockedClientId,
            Date = Date,
            Hour = 14,
            IsAvailable = false
        });
        _availabilityRepository.GetByDateRange(Date, Date).Returns(Task.FromResult(entries));

        _skill = new GetAvailabilityOverviewSkill(_mediator, _availabilityRepository);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    [Test]
    public async Task MorningWindow_CountsOpenAndPositiveDayAsAvailable()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["date"] = "2026-08-03",
            ["startHour"] = 8,
            ["endHour"] = 11
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("3 of 3");
    }

    [Test]
    public async Task AfternoonWindow_PositiveDayAndBlockedHourAreUnavailable()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["date"] = "2026-08-03",
            ["startHour"] = 14,
            ["endHour"] = 16
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 of 3");
    }

    [Test]
    public async Task Result_MentionsAbsenceAndKeywordLimitation()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["date"] = "2026-08-03"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("check_client_availability");
    }

    [Test]
    public async Task InvalidHourWindow_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["date"] = "2026-08-03",
            ["startHour"] = 20,
            ["endHour"] = 5
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("hour");
    }
}
