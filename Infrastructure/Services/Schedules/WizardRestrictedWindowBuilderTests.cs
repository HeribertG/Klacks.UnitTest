// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WizardRestrictedWindowBuilder (K16 GA context): group-tag scope resolution into the concrete
/// restricted shift-id set, empty-tag = all period shifts, non-matching tag = no window, period-overlap
/// validity, and the scenario-token GroupItem being resolved by the token-independent (shift-id) read.
/// </summary>

using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardRestrictedWindowBuilderTests
{
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly Until = new(2026, 7, 31);

    private DataBaseContext _context = null!;
    private IRestrictedTimeWindowRuleRepository _ruleRepository = null!;
    private WizardRestrictedWindowBuilder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        _ruleRepository = Substitute.For<IRestrictedTimeWindowRuleRepository>();
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestrictedTimeWindowRule>());

        _sut = new WizardRestrictedWindowBuilder(_ruleRepository, _context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void StubRule(string tag) =>
        _ruleRepository.GetAllActiveAsync().Returns(new List<RestrictedTimeWindowRule>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SeasonFromMonth = 6, SeasonFromDay = 15, SeasonToMonth = 9, SeasonToDay = 15,
                DailyStart = new TimeOnly(12, 30), DailyEnd = new TimeOnly(15, 0),
                AppliesToGroupTag = tag,
            },
        });

    private void SeedGroupLink(string groupName, Guid shiftId, Guid? analyseToken = null, DateTime? validFrom = null, DateTime? validUntil = null)
    {
        var groupId = Guid.NewGuid();
        _context.Group.Add(new Group { Id = groupId, Name = groupName });
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftId,
            AnalyseToken = analyseToken,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
        });
        _context.SaveChanges();
    }

    [Test]
    public async Task BuildAsync_TagMatch_ResolvesShiftIntoRestrictedSet()
    {
        var shiftId = Guid.NewGuid();
        StubRule("outdoor");
        SeedGroupLink("Outdoor", shiftId);

        var windows = await _sut.BuildAsync([shiftId], From, Until, CancellationToken.None);

        windows.ShouldHaveSingleItem().RestrictedShiftIds.ShouldContain(shiftId);
    }

    [Test]
    public async Task BuildAsync_EmptyTag_RestrictsAllPeriodShifts()
    {
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();
        StubRule(string.Empty);

        var windows = await _sut.BuildAsync([shiftA, shiftB], From, Until, CancellationToken.None);

        var restricted = windows.ShouldHaveSingleItem().RestrictedShiftIds;
        restricted.ShouldContain(shiftA);
        restricted.ShouldContain(shiftB);
    }

    [Test]
    public async Task BuildAsync_NonMatchingTag_ProducesNoWindow()
    {
        StubRule("indoor");
        SeedGroupLink("outdoor", Guid.NewGuid());

        var windows = await _sut.BuildAsync([Guid.NewGuid()], From, Until, CancellationToken.None);

        windows.ShouldBeEmpty();
    }

    [Test]
    public async Task BuildAsync_ScenarioTokenGroupItem_IsResolvedByShiftId()
    {
        var cloneShiftId = Guid.NewGuid();
        StubRule("outdoor");
        SeedGroupLink("outdoor", cloneShiftId, analyseToken: Guid.NewGuid());

        var windows = await _sut.BuildAsync([cloneShiftId], From, Until, CancellationToken.None);

        windows.ShouldHaveSingleItem().RestrictedShiftIds.ShouldContain(cloneShiftId);
    }

    [Test]
    public async Task BuildAsync_LinkValidityOutsidePeriod_NotResolved()
    {
        var shiftId = Guid.NewGuid();
        StubRule("outdoor");
        SeedGroupLink("outdoor", shiftId, validUntil: new DateTime(2026, 6, 1));

        var windows = await _sut.BuildAsync([shiftId], From, Until, CancellationToken.None);

        windows.ShouldBeEmpty();
    }
}
