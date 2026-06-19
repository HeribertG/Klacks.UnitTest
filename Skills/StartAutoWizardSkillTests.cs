// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for StartAutoWizardSkill - parameter validation, group lookup, and agent/shift
/// auto-resolution. The actual AutoWizardJobRunner is fully mocked.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class StartAutoWizardSkillTests
{
    private IAutoWizardJobRunner _runner = null!;
    private IGroupRepository _groupRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftScheduleRepository _shiftScheduleRepository = null!;
    private StartAutoWizardSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _runner = Substitute.For<IAutoWizardJobRunner>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();

        _skill = new StartAutoWizardSkill(_runner, _groupRepository, _clientRepository, _shiftScheduleRepository);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "Admin" }
        };
    }

    [Test]
    public async Task ExecuteAsync_InvalidGroupId_ReturnsError()
    {
        var parameters = new Dictionary<string, object>
        {
            { "groupId", "not-a-uuid" },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid groupId");
    }

    [Test]
    public async Task ExecuteAsync_PeriodFromAfterPeriodUntil_ReturnsError()
    {
        var parameters = new Dictionary<string, object>
        {
            { "groupId", Guid.NewGuid().ToString() },
            { "periodFrom", "2026-05-31" },
            { "periodUntil", "2026-05-01" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must be on or before");
    }

    [Test]
    public async Task ExecuteAsync_GroupNotFound_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns((Group?)null);

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task ExecuteAsync_NoAgentsResolved_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Client>());

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No agents resolved");
    }

    [Test]
    public async Task ExecuteAsync_NoShiftsResolved_ReturnsError()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = Guid.NewGuid(), FirstName = "Coline" } });
        _shiftScheduleRepository.GetShiftScheduleAsync(
            Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>(), 0));

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No shifts visible");
    }

    [Test]
    public async Task ExecuteAsync_HappyPath_ReturnsJobIdFromRunner()
    {
        var groupId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _groupRepository.Get(groupId).Returns(new Group { Id = groupId, Name = "Bern" });
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = clientId } });
        _shiftScheduleRepository.GetShiftScheduleAsync(
            Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment> { new() { ShiftId = shiftId } }, 1));
        _runner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var parameters = new Dictionary<string, object>
        {
            { "groupId", groupId.ToString() },
            { "periodFrom", "2026-05-01" },
            { "periodUntil", "2026-05-31" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeTrue();
        result.Data.ShouldNotBeNull();
        await _runner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(r =>
                r.GroupId == groupId &&
                r.PeriodFrom == new DateOnly(2026, 5, 1) &&
                r.PeriodUntil == new DateOnly(2026, 5, 31) &&
                r.AgentIds.Count == 1 &&
                r.ShiftIds!.Count == 1),
            Arg.Any<CancellationToken>());
    }
}
