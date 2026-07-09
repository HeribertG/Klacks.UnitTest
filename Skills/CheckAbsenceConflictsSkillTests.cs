// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CheckAbsenceConflictsSkill — verifies the feasibility verdict for clean periods,
/// conflict reporting for overlapping planned/booked absences, membership-window warnings, the
/// scheduled-work note, and error handling for unknown clients.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CheckAbsenceConflictsSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private CheckAbsenceConflictsSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();

        _clientRepository.Get(ClientId).Returns(BuildClient());
        _absenceRepository.List().Returns(new List<Absence>
        {
            new() { Id = AbsenceId, Name = new MultiLanguage { De = "Ferien" } }
        });
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>());
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());
        _workRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());

        _skill = new CheckAbsenceConflictsSkill(
            _clientRepository, _breakPlaceholderRepository, _breakRepository, _workRepository, _absenceRepository);
    }

    private static Client BuildClient() => new()
    {
        Id = ClientId,
        FirstName = "Anna",
        Name = "Muster",
        Membership = new Membership
        {
            ClientId = ClientId,
            ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = null
        }
    };

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(string from = "2026-08-03", string until = "2026-08-07") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["fromDate"] = from,
        ["untilDate"] = until
    };

    [Test]
    public async Task CleanPeriod_IsFeasible()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("free of absence conflicts");
    }

    [Test]
    public async Task OverlappingPlannedAbsence_ReportsConflict()
    {
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ClientId = ClientId,
                    AbsenceId = AbsenceId,
                    From = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
                    Until = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)
                }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("overlapping planned absence");
    }

    [Test]
    public async Task BookedAbsenceInPeriod_ReportsConflict()
    {
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>
            {
                new() { Id = Guid.NewGuid(), ClientId = ClientId, AbsenceId = AbsenceId, CurrentDate = new DateOnly(2026, 8, 4) }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("booked absence");
    }

    [Test]
    public async Task PeriodBeforeMembership_ReportsMembershipProblem()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2019-12-29", until: "2019-12-31"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("membership");
    }

    [Test]
    public async Task ScheduledWorkOnly_IsFeasibleWithReplanningNote()
    {
        _workRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>
            {
                new() { Id = Guid.NewGuid(), ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 4) }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("free of absence conflicts");
        result.Message.ShouldContain("re-planned");
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Get(ClientId).Returns(Task.FromResult<Client?>(null));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2026-08-07", until: "2026-08-03"));

        result.Success.ShouldBeFalse();
    }
}
