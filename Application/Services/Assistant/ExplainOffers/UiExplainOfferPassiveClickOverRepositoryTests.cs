// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the passive-click race of UiExplainOfferService over the real ProactiveTriggerDispatchRepository on EF InMemory:
/// a competing click stores the same day's row between the repository's dedup read and its insert, so the insert is
/// rejected like PostgreSQL's unique index would; the click then counts as recorded (one row, no exception). A rejected
/// insert without such a row is not swallowed and escapes as EF Core's DbUpdateException (the error middleware answers
/// 400), and the rejected row is not left tracked.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.ExplainOffers;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.ExplainOffers;

[TestFixture]
public class UiExplainOfferPassiveClickOverRepositoryTests
{
    private const string PassiveTopic = UiExplainOfferTopics.WorkBookingRefusalKey;
    private const string PassiveKind = UiExplainOfferTopics.TriggerKindPrefix + UiExplainOfferTopics.WorkBookingRefusalKey;
    private const string TodaysDedupKey = PassiveTopic + ":accepted:2026-10-19";

    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly CompanyToday = new(2026, 10, 19);

    private DbContextOptions<DataBaseContext> _options = null!;
    private SaveChangesFailingDataBaseContext _context = null!;
    private UiExplainOfferService _sut = null!;
    private string _userId = null!;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid().ToString();
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new SaveChangesFailingDataBaseContext(_options, Substitute.For<IHttpContextAccessor>());

        var governance = Substitute.For<IProactiveGovernanceResolver>();
        governance.IsKillSwitchActiveAsync(Arg.Any<CancellationToken>()).Returns(false);
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _sut = new UiExplainOfferService(
            new ProactiveTriggerDispatchRepository(_context, new SettableTimeProvider(Now)),
            Substitute.For<IAgentTriggerPreferenceService>(),
            new ExplainOffersSwitch(Substitute.For<ISettingsReader>(), governance),
            Substitute.For<IExplainDepthResolver>(),
            new SettableTimeProvider(Now),
            companyClock,
            Substitute.For<ILogger<UiExplainOfferService>>());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task PassiveClick_ACompetingClickStoredTheRowFirst_CountsAsRecorded()
    {
        _context.FailNextSave = () =>
        {
            StoreCompetingClick();
            return SaveChangesFailingDataBaseContext.UniqueViolation();
        };

        (await _sut.RecordReactionAsync(_userId, PassiveTopic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();

        StoredRows().ShouldBe(1);
    }

    [Test]
    public async Task PassiveClick_RejectedInsertWithoutACompetingRow_EscapesAsEfCoreDbUpdateException()
    {
        _context.FailNextSave = SaveChangesFailingDataBaseContext.UniqueViolation;

        var exception = await Should.ThrowAsync<Exception>(
            () => _sut.RecordReactionAsync(_userId, PassiveTopic, UiExplainOfferOutcomes.Accepted));

        exception.ShouldBeOfType<DbUpdateException>();
        StoredRows().ShouldBe(0);
        _context.ChangeTracker.Entries<ProactiveTriggerDispatchRow>().ShouldBeEmpty();
    }

    [Test]
    public async Task PassiveClick_SecondClickOfTheDay_IsANoOp()
    {
        (await _sut.RecordReactionAsync(_userId, PassiveTopic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();
        (await _sut.RecordReactionAsync(_userId, PassiveTopic, UiExplainOfferOutcomes.Accepted)).ShouldBeTrue();

        StoredRows().ShouldBe(1);
    }

    private void StoreCompetingClick()
    {
        using var competitor = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        competitor.AgentTriggerDispatches.Add(new ProactiveTriggerDispatchRow
        {
            UserId = _userId,
            TriggerKind = PassiveKind,
            DedupKey = TodaysDedupKey,
            Severity = AgentTriggerSeverity.Low,
        });
        competitor.SaveChanges();
    }

    private int StoredRows()
    {
        using var reader = new DataBaseContext(_options, Substitute.For<IHttpContextAccessor>());
        return reader.AgentTriggerDispatches.Count(row => row.UserId == _userId && row.TriggerKind == PassiveKind);
    }
}
