// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetAiGuidelinesSkill, which retrieves AI guidelines via IGlobalAgentRuleRepository.
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
public class GetAiGuidelinesSkillTests
{
    private IGlobalAgentRuleRepository _repository = null!;
    private GetAiGuidelinesSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IGlobalAgentRuleRepository>();
        _skill = new GetAiGuidelinesSkill(_repository);
        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "testuser",
            UserPermissions = new[] { "CanViewSettings" }
        };
    }

    [Test]
    public async Task ExecuteAsync_NoActiveGuidelines_ReturnsNotConfigured()
    {
        _repository.GetRuleAsync(GlobalAgentRuleNames.AiGuidelines, Arg.Any<CancellationToken>())
            .Returns((GlobalAgentRule?)null);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("No AI guidelines");
        await _repository.Received(1).GetRuleAsync(GlobalAgentRuleNames.AiGuidelines, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithInactiveRule_ReturnsNotConfigured()
    {
        var rule = new GlobalAgentRule
        {
            Id = Guid.NewGuid(),
            Name = GlobalAgentRuleNames.AiGuidelines,
            Content = "- Some content",
            IsActive = false,
            Source = "seed"
        };
        _repository.GetRuleAsync(GlobalAgentRuleNames.AiGuidelines, Arg.Any<CancellationToken>())
            .Returns(rule);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("No AI guidelines");
    }

    [Test]
    public async Task ExecuteAsync_WithActiveGuidelines_ReturnsData()
    {
        var rule = new GlobalAgentRule
        {
            Id = Guid.NewGuid(),
            Name = GlobalAgentRuleNames.AiGuidelines,
            Content = "- Be polite\n- Be professional",
            IsActive = true,
            Source = "seed"
        };
        _repository.GetRuleAsync(GlobalAgentRuleNames.AiGuidelines, Arg.Any<CancellationToken>())
            .Returns(rule);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("characters");
        result.Data.ShouldNotBeNull();
    }
}
