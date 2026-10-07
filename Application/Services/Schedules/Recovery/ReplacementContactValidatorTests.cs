// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the contact-attempt validation: hidden or missing employees, shifts, groups, scenarios and
/// absence types are all answered with not found; a scenario clone shift is refused; implausible dates and a
/// candidate equal to the absent employee are invalid; a fully visible request passes.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Schedules.Recovery;

[TestFixture]
public class ReplacementContactValidatorTests
{
    private static readonly DateOnly Today = new(2026, 3, 10);
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid CandidateId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid HiddenGroupId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly Guid Token = Guid.NewGuid();

    private IClientVisibilityGuard _clientGuard = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private ReplacementContactValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _clientGuard = Substitute.For<IClientVisibilityGuard>();
        _clientGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);

        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != HiddenGroupId);
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.GetNoTracking(ShiftId).Returns(new Shift { Id = ShiftId, Name = "Early" });
        _shiftRepository.GetGroupsForShift(ShiftId).Returns([new Group { Id = GroupId }]);

        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.Exists(Arg.Any<Guid>()).Returns(true);

        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepository.GetByTokenAsync(Token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = Token, GroupId = GroupId });

        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _absenceRepository.Exists(AbsenceId).Returns(true);

        _validator = new ReplacementContactValidator(
            _clientGuard, _groupGuard, _shiftRepository, _groupRepository, _scenarioRepository, _absenceRepository,
            new FixedCompanyClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero)));
    }

    [Test]
    public async Task VisibleRequest_Passes()
    {
        await Should.NotThrowAsync(() => _validator.ValidateAsync(Request()));
    }

    [Test]
    public async Task HiddenEmployee_IsNotFound()
    {
        _clientGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request()));
    }

    [Test]
    public async Task MissingShift_CloneShift_AndShiftOfAHiddenGroup_AreNotFound()
    {
        var cloneId = Guid.NewGuid();
        _shiftRepository.GetNoTracking(cloneId).Returns(new Shift { Id = cloneId, Name = "Clone", AnalyseToken = Token, ScenarioSourceShiftId = ShiftId });
        var hiddenShiftId = Guid.NewGuid();
        _shiftRepository.GetNoTracking(hiddenShiftId).Returns(new Shift { Id = hiddenShiftId, Name = "Hidden" });
        _shiftRepository.GetGroupsForShift(hiddenShiftId).Returns([new Group { Id = HiddenGroupId }]);

        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { ShiftId = Guid.NewGuid() }));
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { ShiftId = cloneId }));
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { ShiftId = hiddenShiftId }));
    }

    [Test]
    public async Task HiddenOrMissingGroup_IsNotFound()
    {
        _groupRepository.Exists(GroupId).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { GroupId = HiddenGroupId }));
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { GroupId = GroupId }));
    }

    [Test]
    public async Task UnknownScenario_ScenarioOfAHiddenGroup_AndGroupLessScenarioForARestrictedCaller_AreNotFound()
    {
        var hiddenToken = Guid.NewGuid();
        _scenarioRepository.GetByTokenAsync(hiddenToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = hiddenToken, GroupId = HiddenGroupId });
        var groupLessToken = Guid.NewGuid();
        _scenarioRepository.GetByTokenAsync(groupLessToken, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = groupLessToken });

        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { AnalyseToken = Guid.NewGuid() }));
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { AnalyseToken = hiddenToken }));
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { AnalyseToken = groupLessToken }));
    }

    [Test]
    public async Task UnknownAbsenceType_IsNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => _validator.ValidateAsync(Request() with { AbsenceId = Guid.NewGuid() }));
    }

    [Test]
    public async Task ImplausibleDates_AndCandidateEqualToTheAbsentEmployee_AreInvalid()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _validator.ValidateAsync(
            Request() with { Date = Today.AddDays(-ReplacementRequestLimits.MaxContactPastDays - 1) }));
        await Should.ThrowAsync<InvalidRequestException>(() => _validator.ValidateAsync(
            Request() with { Date = Today.AddDays(ReplacementRequestLimits.MaxContactFutureDays + 1) }));
        await Should.ThrowAsync<InvalidRequestException>(() => _validator.ValidateAsync(
            Request() with { CandidateClientId = AbsentId }));
    }

    private static RecordReplacementContactRequest Request() => new(
        AbsentId, CandidateId, ShiftId, Today, new TimeOnly(8, 0), new TimeOnly(16, 0),
        GroupId, AbsenceId, Token, ReplacementRequestOutcome.Declined);
}
