// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the replacement request book endpoints' handlers: recording an answer (Proposed refused, a missing
/// row and a row with a hidden candidate both "not found", author and time stamped), the contact attempt on an
/// alternative (upsert, PlannerDialog source, hidden employees refused like missing ones), the list (rows with a
/// hidden candidate left out, short notice judged with the setting) and the planner candidate search (phones
/// added from the visibility-aware resolver, the search itself unchanged).
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class ReplacementRequestHandlersTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid VisibleCandidateId = Guid.NewGuid();
    private static readonly Guid HiddenCandidateId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private ReplacementRequestRepository _repository = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ISettingsReader _settingsReader = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IReplacementContactValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ReplacementRequestRepository(_context, Substitute.For<ILogger<ReplacementRequest>>());

        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != HiddenCandidateId);
        _visibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => !ci.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(HiddenCandidateId));
        _visibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ReplacementRequest>>(), Arg.Any<Func<ReplacementRequest, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<ReplacementRequest>>(0)
                .Where(r => ci.ArgAt<Func<ReplacementRequest, Guid>>(1)(r) != HiddenCandidateId)
                .ToList());

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => _context.SaveChanges());

        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _validator = Substitute.For<IReplacementContactValidator>();
        _validator.ValidateAsync(Arg.Any<RecordReplacementContactRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<RecordReplacementContactRequest>(0).CandidateClientId == HiddenCandidateId
                ? Task.FromException(new KeyNotFoundException("Client not found"))
                : Task.CompletedTask);

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())]);
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task SetOutcome_RecordsTheAnswerWithAuthorAndTime()
    {
        var rowId = await SeedAsync(VisibleCandidateId);

        var result = await OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(rowId, ReplacementRequestOutcome.Declined), CancellationToken.None);

        result.Outcome.ShouldBe(ReplacementRequestOutcome.Declined);
        var row = await _context.ReplacementRequests.SingleAsync(r => r.Id == rowId);
        row.Outcome.ShouldBe(ReplacementRequestOutcome.Declined);
        row.OutcomeAtUtc.ShouldBe(Now);
        row.OutcomeByUserId.ShouldBe(UserId);
    }

    [Test]
    public async Task SetOutcome_Proposed_IsRefusedAsInvalid()
    {
        var rowId = await SeedAsync(VisibleCandidateId);

        await Should.ThrowAsync<InvalidRequestException>(() => OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(rowId, ReplacementRequestOutcome.Proposed), CancellationToken.None));
    }

    [Test]
    public async Task SetOutcome_HiddenCandidate_IsAnsweredExactlyLikeAMissingRow()
    {
        var hiddenRowId = await SeedAsync(HiddenCandidateId);

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(hiddenRowId, ReplacementRequestOutcome.Accepted), CancellationToken.None));
        var missingId = Guid.NewGuid();
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(missingId, ReplacementRequestOutcome.Accepted), CancellationToken.None));

        hidden.Message.Replace(hiddenRowId.ToString(), string.Empty)
            .ShouldBe(missing.Message.Replace(missingId.ToString(), string.Empty));
        (await _context.ReplacementRequests.SingleAsync(r => r.Id == hiddenRowId)).Outcome
            .ShouldBe(ReplacementRequestOutcome.Proposed);
    }

    [Test]
    public async Task RecordContact_NewAlternative_CreatesAPlannerDialogRowWithTheAnswer()
    {
        var token = Guid.NewGuid();

        var result = await ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(VisibleCandidateId, token, ReplacementRequestOutcome.NotReached)),
            CancellationToken.None);

        var row = await _context.ReplacementRequests.SingleAsync();
        row.Id.ShouldBe(result.Id);
        row.Source.ShouldBe(ReplacementRequestSource.PlannerDialog);
        row.Outcome.ShouldBe(ReplacementRequestOutcome.NotReached);
        row.AnalyseToken.ShouldBe(token);
        row.OutcomeByUserId.ShouldBe(UserId);
        row.ShiftStartUtc.ShouldBe(new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task RecordContact_SameSlotAgain_UpdatesTheExistingRow()
    {
        var token = Guid.NewGuid();
        var rowId = await SeedAsync(VisibleCandidateId, token);

        await ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(VisibleCandidateId, token, ReplacementRequestOutcome.Accepted)),
            CancellationToken.None);

        var row = await _context.ReplacementRequests.SingleAsync();
        row.Id.ShouldBe(rowId);
        row.Outcome.ShouldBe(ReplacementRequestOutcome.Accepted);
        row.Source.ShouldBe(ReplacementRequestSource.RecoveryEngine);
    }

    [Test]
    public async Task RecordContact_HiddenCandidate_IsRefusedLikeAnUnknownEmployee()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(HiddenCandidateId, Guid.NewGuid(), ReplacementRequestOutcome.Declined)),
            CancellationToken.None));

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task List_LeavesOutRowsWithAHiddenCandidate()
    {
        await SeedAsync(VisibleCandidateId);
        await SeedAsync(HiddenCandidateId);

        var rows = await ListHandler().Handle(
            new ListReplacementRequestsQuery(AbsentId, null, null, null), CancellationToken.None);

        rows.ShouldHaveSingleItem().CandidateClientId.ShouldBe(VisibleCandidateId);
    }

    [Test]
    public async Task List_JudgesShortNoticeWithTheConfiguredThreshold()
    {
        await SeedAsync(VisibleCandidateId, reportedHoursBeforeStart: 30);

        var defaultRows = await ListHandler().Handle(new ListReplacementRequestsQuery(null, Day, Day, null), CancellationToken.None);
        _settingsReader.GetSetting(SettingKeys.REPLACEMENT_SHORT_NOTICE_HOURS)
            .Returns(new SettingsEntity { Type = SettingKeys.REPLACEMENT_SHORT_NOTICE_HOURS, Value = "24" });
        var tightenedRows = await ListHandler().Handle(new ListReplacementRequestsQuery(null, Day, Day, null), CancellationToken.None);

        defaultRows.ShouldHaveSingleItem().IsShortNotice.ShouldBeTrue();
        tightenedRows.ShouldHaveSingleItem().IsShortNotice.ShouldBeFalse();
    }

    [Test]
    public async Task SetOutcome_HiddenAbsentEmployee_IsAnsweredLikeAMissingRow()
    {
        var rowId = await SeedAsync(VisibleCandidateId, absentId: HiddenCandidateId);

        await Should.ThrowAsync<KeyNotFoundException>(() => OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(rowId, ReplacementRequestOutcome.Accepted), CancellationToken.None));
    }

    [Test]
    public async Task SetOutcome_RowAlreadyApplied_IsRefused()
    {
        var rowId = await SeedAsync(VisibleCandidateId, appliedAtUtc: Now.AddHours(-1));

        await Should.ThrowAsync<InvalidRequestException>(() => OutcomeHandler().Handle(
            new SetReplacementRequestOutcomeCommand(rowId, ReplacementRequestOutcome.Declined), CancellationToken.None));
    }

    [Test]
    public async Task RecordContact_RowOfAnotherAbsentEmployee_IsAnsweredLikeAnUnknownEmployee()
    {
        var token = Guid.NewGuid();
        await SeedAsync(VisibleCandidateId, token, absentId: Guid.NewGuid());

        await Should.ThrowAsync<KeyNotFoundException>(() => ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(VisibleCandidateId, token, ReplacementRequestOutcome.Accepted)),
            CancellationToken.None));
    }

    [Test]
    public async Task RecordContact_NewRow_TakesTheReportTimeAlreadyRecordedForThisAbsence()
    {
        var token = Guid.NewGuid();
        await SeedAsync(Guid.NewGuid(), token, reportedHoursBeforeStart: 5);

        var result = await ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(VisibleCandidateId, token, ReplacementRequestOutcome.NotReached)),
            CancellationToken.None);

        result.ReportedAtUtc.ShouldBe(new DateTime(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task RecordContact_AppliedRow_IsRefused()
    {
        var token = Guid.NewGuid();
        await SeedAsync(VisibleCandidateId, token, appliedAtUtc: Now.AddHours(-1));

        await Should.ThrowAsync<InvalidRequestException>(() => ContactHandler().Handle(
            new RecordReplacementContactCommand(Contact(VisibleCandidateId, token, ReplacementRequestOutcome.Declined)),
            CancellationToken.None));
    }

    [Test]
    public async Task List_LeavesOutRowsWithAHiddenAbsentEmployee()
    {
        await SeedAsync(VisibleCandidateId);
        await SeedAsync(VisibleCandidateId, absentId: HiddenCandidateId);

        var rows = await ListHandler().Handle(new ListReplacementRequestsQuery(null, Day, Day, null), CancellationToken.None);

        rows.ShouldHaveSingleItem().AbsentClientId.ShouldBe(AbsentId);
    }

    [Test]
    public async Task List_WithoutAnyBound_OrWithATooLongRange_IsRefused()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => ListHandler().Handle(
            new ListReplacementRequestsQuery(null, null, null, null), CancellationToken.None));
        await Should.ThrowAsync<InvalidRequestException>(() => ListHandler().Handle(
            new ListReplacementRequestsQuery(null, Day, Day.AddDays(ReplacementRequestLimits.MaxListSpanDays), null), CancellationToken.None));
        await Should.ThrowAsync<InvalidRequestException>(() => ListHandler().Handle(
            new ListReplacementRequestsQuery(null, Day, null, null), CancellationToken.None));
    }
    [Test]
    public async Task Candidates_AddPhonesFromTheResolver_AndKeepTheSearchResult()
    {
        var mediator = Substitute.For<IMediator>();
        var search = new FindReplacementQuery(ShiftId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), Guid.NewGuid(), null);
        var excluded = new ExcludedCandidate(Guid.NewGuid(), "Eve", "absent");
        mediator.Send(search, Arg.Any<CancellationToken>()).Returns(new ReplacementSearchResult(
            [Candidate(VisibleCandidateId), Candidate(HiddenCandidateId)], [excluded]));
        var resolver = Substitute.For<IReplacementContactPhoneResolver>();
        resolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [VisibleCandidateId] = "079 111 22 33" });

        var result = await new GetReplacementCandidatesQueryHandler(mediator, resolver)
            .Handle(new GetReplacementCandidatesQuery(search), CancellationToken.None);

        result.Eligible.Single(c => c.ClientId == VisibleCandidateId).Phone.ShouldBe("079 111 22 33");
        result.Eligible.Single(c => c.ClientId == HiddenCandidateId).Phone.ShouldBeNull();
        result.Excluded.ShouldHaveSingleItem().ShouldBe(excluded);
    }

    private SetReplacementRequestOutcomeCommandHandler OutcomeHandler()
        => new(_repository, _visibilityGuard, _unitOfWork, _settingsReader, new SettableTimeProvider(Now), _httpContextAccessor);

    private RecordReplacementContactCommandHandler ContactHandler()
        => new(_repository, _validator, _unitOfWork, _settingsReader,
            new FixedCompanyClock(new DateTimeOffset(Now), TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich")),
            new SettableTimeProvider(Now), _httpContextAccessor);

    private ListReplacementRequestsQueryHandler ListHandler()
        => new(_repository, _visibilityGuard, _settingsReader);

    private static RecordReplacementContactRequest Contact(Guid candidateId, Guid token, ReplacementRequestOutcome outcome)
        => new(AbsentId, candidateId, ShiftId, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), null, null, token, outcome);

    private static ReplacementCandidate Candidate(Guid clientId)
        => new(clientId, "Bob", false, new List<ScheduleValidationNotificationDto>(), 0m);

    private async Task<Guid> SeedAsync(
        Guid candidateId, Guid? token = null, int reportedHoursBeforeStart = 2, Guid? absentId = null, DateTime? appliedAtUtc = null)
    {
        var shiftStart = new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc);
        var row = new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            AbsentClientId = absentId ?? AbsentId,
            CandidateClientId = candidateId,
            ShiftId = ShiftId,
            Date = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            Source = ReplacementRequestSource.RecoveryEngine,
            Outcome = ReplacementRequestOutcome.Proposed,
            ReportedAtUtc = shiftStart.AddHours(-reportedHoursBeforeStart),
            ShiftStartUtc = shiftStart,
            AnalyseToken = token ?? Guid.NewGuid(),
            AppliedAtUtc = appliedAtUtc
        };
        _context.ReplacementRequests.Add(row);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        return row.Id;
    }
}
