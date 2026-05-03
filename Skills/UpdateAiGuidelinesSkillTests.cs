// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateAiGuidelinesSkill, which upserts AI guidelines via IGlobalAgentRuleRepository.
/// </summary>
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAiGuidelinesSkillTests
{
    private IGlobalAgentRuleRepository _repository = null!;
    private UpdateAiGuidelinesSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IGlobalAgentRuleRepository>();
        _repository.UpsertRuleAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => new GlobalAgentRule
            {
                Id = Guid.NewGuid(),
                Name = callInfo.ArgAt<string>(0),
                Content = callInfo.ArgAt<string>(1),
                IsActive = true,
                Source = callInfo.ArgAt<string?>(3)
            });

        _skill = new UpdateAiGuidelinesSkill(_repository);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "admin",
            UserPermissions = new[] { "CanEditSettings" }
        };
    }

    [Test]
    public async Task ExecuteAsync_WithGuidelines_CallsUpsertWithCorrectName()
    {
        var parameters = new Dictionary<string, object>
        {
            { "guidelines", "- New rule 1\n- New rule 2" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeTrue();
        await _repository.Received(1).UpsertRuleAsync(
            GlobalAgentRuleNames.AiGuidelines,
            "- New rule 1\n- New rule 2",
            Arg.Any<int>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithGuidelines_ReturnsSuccessWithContentLength()
    {
        var content = "- New rule 1\n- New rule 2";
        var parameters = new Dictionary<string, object>
        {
            { "guidelines", content }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain(content.Length.ToString());
    }

    [Test]
    public void ExecuteAsync_MissingGuidelinesParam_ThrowsArgumentException()
    {
        var parameters = new Dictionary<string, object>();

        var act = () => _skill.ExecuteAsync(_context, parameters);

        (act.ShouldThrowAsync<ArgumentException>()).Result.Message.ShouldContain("guidelines");
    }
}
