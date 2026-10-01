// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for AddScheduleCommandsRangeSkill — verifies one command per day over the range,
/// skipping of already-present identical commands, the range cap, and keyword validation.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddScheduleCommandsRangeSkillTests
{
    private static readonly ScheduleCommandKeywordSet DefaultKeywords = ScheduleCommandKeywordTestFactory.Default;

    private IScheduleCommandRepository _scheduleCommandRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IScheduleCommandKeywordProvider _keywordProvider = null!;
    private AddScheduleCommandsRangeSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _scheduleCommandRepository = Substitute.For<IScheduleCommandRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();

        _clientRepository.Exists(ClientId).Returns(true);
        _scheduleCommandRepository.GetByClientsAndDateRangeAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords);

        _skill = new AddScheduleCommandsRangeSkill(_scheduleCommandRepository, _clientRepository, SkillClientVisibility.AllVisible(), _unitOfWork, _keywordProvider);
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

    [Test]
    public async Task ConfiguredKeyword_IsAcceptedInsteadOfEnglishDefault()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { FreeToken = "URLAUB" });
        var added = new List<ScheduleCommand>();
        await _scheduleCommandRepository.Add(Arg.Do<ScheduleCommand>(c => added.Add(c)));

        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "urlaub"));

        result.Success.ShouldBeTrue(result.Message);
        added.ShouldAllBe(c => c.CommandKeyword == "URLAUB");
    }

    [Test]
    public async Task EnglishDefault_IsRejected_WhenKeywordWasRenamed()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { FreeToken = "URLAUB" });

        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "FREE"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid commandKeyword");
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task HiddenClient_AnswersLikeUnknownId_WithoutWrite()
    {
        var guard = SkillClientVisibility.Hiding(ClientId);
        var skill = new AddScheduleCommandsRangeSkill(_scheduleCommandRepository, _clientRepository, guard, _unitOfWork, _keywordProvider);
        var unknownId = Guid.NewGuid();
        var unknownParameters = Parameters();
        unknownParameters["clientId"] = unknownId.ToString();

        var hidden = await skill.ExecuteAsync(Context(), Parameters());
        var unknown = await skill.ExecuteAsync(Context(), unknownParameters);

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, ClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownId));
        await guard.Received().IsVisibleAsync(ClientId, Arg.Any<CancellationToken>());
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().GetByClientsAndDateRangeAsync(
            default!, default, default, default, default);
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
