using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using NSubstitute;
using FluentAssertions;

namespace Klacks.UnitTest.Assistant;

[TestFixture]
public class GetAiGuidelinesSkillTests
{
    private IAiGuidelinesRepository _repository = null!;
    private GetAiGuidelinesSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAiGuidelinesRepository>();
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
    public void Name_ReturnsCorrectName()
    {
        _skill.Name.Should().Be("get_ai_guidelines");
    }

    [Test]
    public void Category_IsQuery()
    {
        _skill.Category.Should().Be(Klacks.Api.Domain.Enums.SkillCategory.Query);
    }

    [Test]
    public void RequiredPermissions_ContainsCanViewSettings()
    {
        _skill.RequiredPermissions.Should().Contain("CanViewSettings");
    }

    [Test]
    public void Parameters_IsEmpty()
    {
        _skill.Parameters.Should().BeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_NoActiveGuidelines_ReturnsNotConfigured()
    {
        _repository.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((AiGuidelines?)null);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("No AI guidelines");
        await _repository.Received(1).GetActiveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithActiveGuidelines_ReturnsData()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = "Default Guidelines",
            Content = "- Be polite\n- Be professional",
            IsActive = true,
            Source = "seed"
        };
        _repository.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(guidelines);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Default Guidelines");
        result.Message.Should().Contain("characters");
        result.Data.Should().NotBeNull();
    }
}
