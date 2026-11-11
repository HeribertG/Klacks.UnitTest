// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The chat skills start_wizard1, start_wizard2 and start_wizard3 plan for the group they were called with: each hands
/// its groupId to the stage request, so the stage closes the days outside an employee's period in that group, like the
/// AutoWizard chain does.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StartWizardSkillsGroupScopeTests
{
    private static readonly DateOnly PeriodFrom = new(2026, 12, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 12, 31);

    private readonly Guid _groupId = Guid.NewGuid();
    private IGroupPlanningAgentRepository _planningAgentRepository = null!;
    private SkillRosterOrderResolver _rosterOrderResolver = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _planningAgentRepository = Substitute.For<IGroupPlanningAgentRepository>();
        _planningAgentRepository.GetAgentIdsAsync(_groupId, PeriodFrom, PeriodUntil, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });
        var sortPreferenceRepository = Substitute.For<IClientSortPreferenceRepository>();
        sortPreferenceRepository.GetByUserAndGroupAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientSortPreference>());
        _rosterOrderResolver = new SkillRosterOrderResolver(sortPreferenceRepository);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "Admin" },
        };
    }

    [Test]
    public async Task StartWizard1_HandsTheGroupToTheWizardRequest()
    {
        var runner = Substitute.For<IWizardJobRunner>();
        var shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();
        shiftScheduleRepository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment> { new() { ShiftId = Guid.NewGuid() } }, 1));

        var result = await new StartWizard1Skill(runner, _planningAgentRepository, shiftScheduleRepository, _rosterOrderResolver)
            .ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(Arg.Is<WizardContextRequest>(r => r.GroupId == _groupId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWizard2_HandsTheGroupToTheHarmonizerRequest()
    {
        var runner = Substitute.For<IHarmonizerJobRunner>();

        var result = await new StartWizard2Skill(runner, _planningAgentRepository, _rosterOrderResolver).ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(Arg.Is<HarmonizerContextRequest>(r => r.GroupId == _groupId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWizard3_HandsTheGroupToTheHolisticRunInput()
    {
        var runner = Substitute.For<IHolisticHarmonizerJobRunner>();

        var result = await new StartWizard3Skill(runner, _planningAgentRepository, _rosterOrderResolver).ExecuteAsync(_context, Parameters());

        result.Success.ShouldBeTrue(result.Message);
        await runner.Received(1).StartAsync(Arg.Is<HolisticHarmonizerRunInput>(r => r.GroupId == _groupId), Arg.Any<CancellationToken>());
    }

    private Dictionary<string, object> Parameters() => new()
    {
        { "groupId", _groupId.ToString() },
        { "periodFrom", "2026-12-01" },
        { "periodUntil", "2026-12-31" },
    };
}
