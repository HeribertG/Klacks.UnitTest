// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CheckAbsenceConflictsSkill.ExecuteAsync, written before its long body was split
/// into private steps: the exact verdict texts (free, free with the re-planning note, issues listing), the exact
/// serialized result data, each membership warning and their precedence, the absence-type label fallback, that
/// scenario work is ignored, the ranges handed to the repositories, the order in which the repositories are
/// consulted and the refusals that stop before any of them.
/// </summary>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CheckAbsenceConflictsSkillCharacterizationTests
{
    private static readonly Guid ClientId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid VacationId = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid PlaceholderId = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid BreakId = Guid.Parse("b0000000-0000-0000-0000-000000000004");
    private static readonly Guid WorkId = Guid.Parse("b0000000-0000-0000-0000-000000000005");
    private static readonly Guid EnglishOnlyId = Guid.Parse("b0000000-0000-0000-0000-000000000006");
    private static readonly Guid UnnamedId = Guid.Parse("b0000000-0000-0000-0000-000000000007");

    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private CheckAbsenceConflictsSkill _skill = null!;
    private List<string> _calls = null!;
    private Client? _client;
    private List<BreakPlaceholder> _placeholders = null!;
    private List<Break> _breaks = null!;
    private List<Work> _works = null!;

    [SetUp]
    public void SetUp()
    {
        _calls = [];
        _client = new Client
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
        _placeholders = [];
        _breaks = [];
        _works = [];

        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("visible"); return true; });
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.Get(Arg.Any<Guid>()).Returns(_ => { _calls.Add("client-get"); return _client; });
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _absenceRepository.List().Returns(_ =>
        {
            _calls.Add("absences");
            return new List<Absence>
            {
                new() { Id = VacationId, Name = new MultiLanguage { De = "Ferien", En = "Vacation" } },
                new() { Id = EnglishOnlyId, Name = new MultiLanguage { De = " ", En = "Sick leave" } },
                new() { Id = UnnamedId, Name = new MultiLanguage() }
            };
        });
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("placeholders"); return _placeholders; });
        _breakRepository = Substitute.For<IBreakRepository>();
        _breakRepository
            .GetByClientAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("breaks"); return _breaks; });
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository
            .GetByClientAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("works"); return _works; });

        _skill = new CheckAbsenceConflictsSkill(
            _clientRepository, _visibilityGuard, _breakPlaceholderRepository, _breakRepository, _workRepository, _absenceRepository);
    }

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

    private void AllConflicts()
    {
        _placeholders =
        [
            new()
            {
                Id = PlaceholderId,
                ClientId = ClientId,
                AbsenceId = VacationId,
                From = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc),
                Until = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc)
            }
        ];
        _breaks = [new() { Id = BreakId, ClientId = ClientId, AbsenceId = Guid.NewGuid(), CurrentDate = new DateOnly(2026, 8, 4) }];
        _works =
        [
            new() { Id = WorkId, ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 4), LockLevel = WorkLockLevel.Confirmed },
            new() { Id = Guid.NewGuid(), ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 5), AnalyseToken = Guid.NewGuid() }
        ];
        _client!.Membership!.ValidUntil = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
    }

    [Test]
    public async Task FreePeriod_ExactMessageDataAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("The period 2026-08-03..2026-08-07 is free of absence conflicts for Anna Muster.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ClientId\":\"" + ClientId + "\",\"ClientName\":\"Anna Muster\",\"FromDate\":\"2026-08-03\"," +
            "\"UntilDate\":\"2026-08-07\",\"Feasible\":true,\"PlannedAbsenceOverlaps\":[],\"BookedAbsenceOverlaps\":[]," +
            "\"ScheduledWorkInPeriod\":[],\"MembershipWarning\":null}");
        _calls.ShouldBe(["visible", "client-get", "absences", "placeholders", "breaks", "works"]);
    }

    [Test]
    public async Task Repositories_GetTheClientAndTheUtcDayRange()
    {
        await _skill.ExecuteAsync(Context(), Parameters());

        var fromUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var untilUtc = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);
        await _breakPlaceholderRepository.Received(1).GetByClientAndRangeAsync(
            ClientId,
            Arg.Is<DateTime>(d => d == fromUtc && d.Kind == DateTimeKind.Utc),
            Arg.Is<DateTime>(d => d == untilUtc && d.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
        await _breakRepository.Received(1).GetByClientAndDateRangeAsync(
            ClientId, new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 7), Arg.Any<CancellationToken>());
        await _workRepository.Received(1).GetByClientAndDateRangeAsync(
            ClientId,
            Arg.Is<DateTime>(d => d == fromUtc && d.Kind == DateTimeKind.Utc),
            Arg.Is<DateTime>(d => d == untilUtc && d.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllConflicts_ExactIssuesMessageAndData_IgnoringScenarioWork()
    {
        AllConflicts();

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "The period 2026-08-03..2026-08-07 has issues for Anna Muster: 1 overlapping planned absence(s), " +
            "1 overlapping booked absence day(s), 1 scheduled work assignment(s) that would need replacement, " +
            "a membership period problem. Relay the details to the user before entering the absence.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ClientId\":\"" + ClientId + "\",\"ClientName\":\"Anna Muster\",\"FromDate\":\"2026-08-03\"," +
            "\"UntilDate\":\"2026-08-07\",\"Feasible\":false," +
            "\"PlannedAbsenceOverlaps\":[{\"PlaceholderId\":\"" + PlaceholderId + "\",\"AbsenceType\":\"Ferien\"," +
            "\"From\":\"2026-08-05\",\"Until\":\"2026-08-10\"}]," +
            "\"BookedAbsenceOverlaps\":[{\"BreakId\":\"" + BreakId + "\",\"AbsenceType\":\"Unknown\",\"Date\":\"2026-08-04\"}]," +
            "\"ScheduledWorkInPeriod\":[{\"WorkId\":\"" + WorkId + "\",\"Date\":\"2026-08-04\",\"LockLevel\":1}]," +
            "\"MembershipWarning\":\"The period ends after the client\\u0027s membership ends (2026-08-05).\"}");
    }

    [Test]
    public async Task ScheduledWorkOnly_IsFeasible_WithTheExactReplanningNote()
    {
        _works = [new() { Id = WorkId, ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 4) }];

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Message.ShouldBe(
            "The period 2026-08-03..2026-08-07 is free of absence conflicts for Anna Muster. " +
            "Note: 1 scheduled work assignment(s) fall into the period and would need to be re-planned (e.g. via cover_absence).");
        JsonSerializer.Serialize(result.Data).ShouldContain("\"Feasible\":true,");
    }

    [Test]
    public async Task OnlyScenarioWork_GivesNoNote()
    {
        _works = [new() { Id = WorkId, ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 4), AnalyseToken = Guid.NewGuid() }];

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Message.ShouldBe("The period 2026-08-03..2026-08-07 is free of absence conflicts for Anna Muster.");
    }

    [Test]
    public async Task AbsenceTypeLabel_PrefersGerman()
    {
        _placeholders = [Placeholder(VacationId)];

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        JsonSerializer.Serialize(result.Data).ShouldContain("\"AbsenceType\":\"Ferien\"");
    }

    [Test]
    public async Task AbsenceTypeLabel_FallsBackToTheNextNonBlankLanguage_ThenToAbsence()
    {
        _placeholders = [Placeholder(EnglishOnlyId), Placeholder(UnnamedId)];

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"AbsenceType\":\"Sick leave\"");
        json.ShouldContain("\"AbsenceType\":\"absence\"");
        result.Message!.ShouldContain("2 overlapping planned absence(s)");
    }

    private static BreakPlaceholder Placeholder(Guid absenceId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        AbsenceId = absenceId,
        From = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc),
        Until = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task NoMembership_HasItsExactWarning()
    {
        _client!.Membership = null;

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Message.ShouldBe(
            "The period 2026-08-03..2026-08-07 has issues for Anna Muster: a membership period problem. " +
            "Relay the details to the user before entering the absence.");
        JsonSerializer.Serialize(result.Data).ShouldContain(
            "\"MembershipWarning\":\"The client has no membership record; plannability cannot be confirmed.\"");
    }

    [Test]
    public async Task StartBeforeMembership_WinsOverEndAfterMembership()
    {
        _client!.Membership!.ValidFrom = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        _client.Membership.ValidUntil = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        JsonSerializer.Serialize(result.Data).ShouldContain(
            "\"MembershipWarning\":\"The period starts before the client\\u0027s membership begins (2026-08-04).\"");
    }

    [Test]
    public async Task PeriodInsideMembership_WithEndDate_HasNoWarning()
    {
        _client!.Membership!.ValidUntil = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        JsonSerializer.Serialize(result.Data).ShouldContain("\"MembershipWarning\":null");
    }

    [Test]
    public async Task ClientNameWithoutFirstName_IsTrimmed()
    {
        _client!.FirstName = string.Empty;

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Message.ShouldBe("The period 2026-08-03..2026-08-07 is free of absence conflicts for Muster.");
    }

    [Test]
    public async Task UntilBeforeFrom_IsRefusedBeforeTheClientLookup()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2026-08-07", until: "2026-08-03"));

            result.Success.ShouldBeFalse();
            result.Message.ShouldBe("untilDate (08/03/2026) must not be before fromDate (08/07/2026).");
            _calls.ShouldBeEmpty();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task UnknownClient_IsRefusedBeforeAnyAbsenceOrWorkLookup()
    {
        _client = null;

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Client {ClientId} not found.");
        _calls.ShouldBe(["visible", "client-get"]);
    }

    [Test]
    public async Task MissingDates_ThrowInDeclarationOrder()
    {
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(
                Context(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString() })))
            .Message.ShouldBe("Required parameter 'fromDate' is missing");
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(
                Context(), new Dictionary<string, object> { ["clientId"] = ClientId.ToString(), ["fromDate"] = "2026-08-03" })))
            .Message.ShouldBe("Required parameter 'untilDate' is missing");
        _calls.ShouldBeEmpty();
    }
}
