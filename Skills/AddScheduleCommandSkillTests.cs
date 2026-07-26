// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddScheduleCommandSkill — verifies keyword validation against the currently
/// configured (admin-renamable) tokens, and the single-day write.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddScheduleCommandSkillTests
{
    private static readonly ScheduleCommandKeywordSet DefaultKeywords = new()
    {
        FreeToken = "FREE",
        NegFreeToken = "-FREE",
        EarlyToken = "EARLY",
        NegEarlyToken = "-EARLY",
        LateToken = "LATE",
        NegLateToken = "-LATE",
        NightToken = "NIGHT",
        NegNightToken = "-NIGHT",
    };

    private IScheduleCommandRepository _scheduleCommandRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IScheduleCommandKeywordProvider _keywordProvider = null!;
    private AddScheduleCommandSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _scheduleCommandRepository = Substitute.For<IScheduleCommandRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();

        _clientRepository.Exists(ClientId).Returns(true);
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords);

        _skill = new AddScheduleCommandSkill(_scheduleCommandRepository, _clientRepository, _unitOfWork, _keywordProvider);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(string keyword = "FREE") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["date"] = "2026-07-10",
        ["commandKeyword"] = keyword
    };

    [Test]
    public async Task ValidKeyword_PlacesCommand()
    {
        ScheduleCommand? added = null;
        await _scheduleCommandRepository.Add(Arg.Do<ScheduleCommand>(c => added = c));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        added.ShouldNotBeNull();
        added!.ClientId.ShouldBe(ClientId);
        added.CommandKeyword.ShouldBe("FREE");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task InvalidKeyword_ReturnsError_WithoutWrite()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "WEEKEND"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid commandKeyword");
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task ConfiguredKeyword_IsAcceptedInsteadOfEnglishDefault()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { FreeToken = "URLAUB" });
        ScheduleCommand? added = null;
        await _scheduleCommandRepository.Add(Arg.Do<ScheduleCommand>(c => added = c));

        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "urlaub"));

        result.Success.ShouldBeTrue(result.Message);
        added!.CommandKeyword.ShouldBe("URLAUB");
    }

    [Test]
    public async Task EnglishDefault_IsRejected_WhenKeywordWasRenamed()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { FreeToken = "URLAUB" });

        var result = await _skill.ExecuteAsync(Context(), Parameters(keyword: "FREE"));

        result.Success.ShouldBeFalse();
        await _scheduleCommandRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }
}
