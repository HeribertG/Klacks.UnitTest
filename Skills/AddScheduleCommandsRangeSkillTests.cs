// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddScheduleCommandsRangeSkill — verifies one command per day over the range,
/// skipping of already-present identical commands, the range cap, and keyword validation.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddScheduleCommandsRangeSkillTests
{
    private IScheduleCommandRepository _scheduleCommandRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddScheduleCommandsRangeSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _scheduleCommandRepository = Substitute.For<IScheduleCommandRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _clientRepository.Exists(ClientId).Returns(true);
        _scheduleCommandRepository.GetByClientsAndDateRangeAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _skill = new AddScheduleCommandsRangeSkill(_scheduleCommandRepository, _clientRepository, _unitOfWork);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(
        string from = "2026-07-10", string until = "2026-07-14", string keyword = "FREE") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["fromDate"] = from,
        ["untilDate"] = until,
        ["commandKeyword"] = keyword
    };

    [Test]
    public async Task ValidRange_PlacesOneCommandPerDay()
    {
        var added = new List<ScheduleCommand>();
        await _scheduleCommandRepository.Add(Arg.Do<ScheduleCommand>(c => added.Add(c)));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        added.Count.ShouldBe(5);
        added.Select(c => c.CurrentDate).ShouldBe(
        [
            new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 11), new DateOnly(2026, 7, 12),
            new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 14)
        ]);
        added.ShouldAllBe(c => c.CommandKeyword == "FREE" && c.ClientId == ClientId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ExistingIdenticalCommands_AreSkipped()
    {
        _scheduleCommandRepository.GetByClientsAndDateRangeAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ScheduleCommand { ClientId = ClientId, CurrentDate = new DateOnly(2026, 7, 11), CommandKeyword = "FREE" },
                new ScheduleCommand { ClientId = ClientId, CurrentDate = new DateOnly(2026, 7, 13), CommandKeyword = "FREE" }
            ]);
        var added = new List<ScheduleCommand>();
        await _scheduleCommandRepository.Add(Arg.Do<ScheduleCommand>(c => added.Add(c)));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        added.Count.ShouldBe(3);
        added.Select(c => c.CurrentDate).ShouldNotContain(new DateOnly(2026, 7, 11));
        added.Select(c => c.CurrentDate).ShouldNotContain(new DateOnly(2026, 7, 13));
    }

    [Test]
    public async Task RangeOverCap_ReturnsError_WithoutWrite()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2026-01-01", until: "2026-12-31"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("92");
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task InvalidKeyword_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "WEEKEND"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid commandKeyword");
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2026-07-14", until: "2026-07-10"));

        result.Success.ShouldBeFalse();
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }
}
