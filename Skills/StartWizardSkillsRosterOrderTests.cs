// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The chat skills start_wizard1, start_wizard2 and start_wizard3 hand the chat user's saved roster order of the group
/// to the stage request and mark it as user-defined, so the stage keeps it instead of ranking by guaranteed hours. The
/// order runs through the real SkillRosterOrderResolver; only the saved-order repository is substituted.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StartWizardSkillsRosterOrderTests
{
    private static readonly DateOnly PeriodFrom = new(2026, 12, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 12, 31);

    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private IGroupPlanningAgentRepository _planningAgentRepository = null!;
    private SkillRosterOrderResolver _rosterOrderResolver = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _planningAgentRepository = Substitute.For<IGroupPlanningAgentRepository>();
        _planningAgentRepository.GetAgentIdsAsync(_groupId, PeriodFrom, PeriodUntil, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _first, _second });

        var sortPreferenceRepository = Substitute.For<IClientSortPreferenceRepository>();
        sortPreferenceRepository.GetByUserAndGroupAsync(_userId.ToString(), _groupId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientSortPreference>
            {
                new() { GroupId = _groupId, ClientId = _second, SortOrder = 0 },
                new() { GroupId = _groupId, ClientId = _first, SortOrder = 1 },
            });
        _rosterOrderResolver = new SkillRosterOrderResolver(sortPreferenceRepository);

        _context = new SkillExecutionContext
        {
            UserId = _userId,
            TenantId = Guid.NewGuid(),
            UserName = "planner",
            UserPermissions = [],
        };
    }

    [Test]
    public async Task StartWizard1_HandsTheSavedOrderToTheWizardRequestAsUserDefined()
    {
        var runner = Substitute.For<IWizardJobRunner>();
        var shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();
        shiftScheduleRepository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment> { new() { ShiftId = Guid.NewGuid() } }, 1));

        var result = await new StartWizard1Skill(runner, _planningAgentRepository, shiftScheduleRepository, _rosterOrderResolver)
            .ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(
            Arg.Is<WizardContextRequest>(r => r.AgentIds.SequenceEqual(new[] { _second, _first }) && r.AgentOrderIsUserDefined),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWizard2_HandsTheSavedOrderToTheHarmonizerRequestAsUserDefined()
    {
        var runner = Substitute.For<IHarmonizerJobRunner>();

        var result = await new StartWizard2Skill(runner, _planningAgentRepository, _rosterOrderResolver)
            .ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(
            Arg.Is<HarmonizerContextRequest>(r => r.AgentIds.SequenceEqual(new[] { _second, _first }) && r.AgentOrderIsUserDefined),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWizard3_HandsTheSavedOrderToTheHolisticRunInputAsUserDefined()
    {
        var runner = Substitute.For<IHolisticHarmonizerJobRunner>();

        var result = await new StartWizard3Skill(runner, _planningAgentRepository, _rosterOrderResolver)
            .ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(
            Arg.Is<HolisticHarmonizerRunInput>(r => r.AgentIds.SequenceEqual(new[] { _second, _first }) && r.AgentOrderIsUserDefined),
            Arg.Any<CancellationToken>());
    }

    private Dictionary<string, object> Parameters() => new()
    {
        { "groupId", _groupId.ToString() },
        { "periodFrom", "2026-12-01" },
        { "periodUntil", "2026-12-31" },
    };
}
