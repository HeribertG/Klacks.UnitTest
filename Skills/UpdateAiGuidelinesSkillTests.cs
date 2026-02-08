using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.AI;
using Klacks.Api.Domain.Models.AI;
using Klacks.Api.Domain.Models.Skills;
using NUnit.Framework;
using NSubstitute;
using FluentAssertions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAiGuidelinesSkillTests
{
    private IAiGuidelinesRepository _repository = null!;
    private UpdateAiGuidelinesSkill _skill = null!;
    private SkillExecutionContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IAiGuidelinesRepository>();
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
    public void Name_ReturnsCorrectName()
    {
        _skill.Name.Should().Be("update_ai_guidelines");
    }

    [Test]
    public void Category_IsCrud()
    {
        _skill.Category.Should().Be(Klacks.Api.Domain.Enums.SkillCategory.Crud);
    }

    [Test]
    public void RequiredPermissions_ContainsCanEditSettings()
    {
        _skill.RequiredPermissions.Should().Contain("CanEditSettings");
    }

    [Test]
    public void Parameters_HasGuidelinesRequired()
    {
        _skill.Parameters.Should().ContainSingle(p => p.Name == "guidelines" && p.Required);
    }

    [Test]
    public void Parameters_HasNameOptional()
    {
        _skill.Parameters.Should().ContainSingle(p => p.Name == "name" && !p.Required);
    }

    [Test]
    public async Task ExecuteAsync_WithGuidelinesOnly_CreatesWithDefaultName()
    {
        var parameters = new Dictionary<string, object>
        {
            { "guidelines", "- New rule 1\n- New rule 2" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("AI Guidelines");
        await _repository.Received(1).DeactivateAllAsync(Arg.Any<CancellationToken>());
        await _repository.Received(1).AddAsync(
            Arg.Is<AiGuidelines>(g =>
                g.Content == "- New rule 1\n- New rule 2" &&
                g.Name == "AI Guidelines" &&
                g.IsActive &&
                g.Source == "chat"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WithNameAndGuidelines_UsesProvidedName()
    {
        var parameters = new Dictionary<string, object>
        {
            { "guidelines", "- Custom rule" },
            { "name", "Custom Guidelines" }
        };

        var result = await _skill.ExecuteAsync(_context, parameters);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Custom Guidelines");
        await _repository.Received(1).AddAsync(
            Arg.Is<AiGuidelines>(g => g.Name == "Custom Guidelines"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void ExecuteAsync_MissingGuidelinesParam_ThrowsArgumentException()
    {
        var parameters = new Dictionary<string, object>();

        var act = () => _skill.ExecuteAsync(_context, parameters);

        act.Should().ThrowAsync<ArgumentException>().WithMessage("*guidelines*");
    }

    [Test]
    public async Task ExecuteAsync_DeactivatesPreviousBeforeAdding()
    {
        var callOrder = new List<string>();
        _repository.When(r => r.DeactivateAllAsync(Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("deactivate"));
        _repository.When(r => r.AddAsync(Arg.Any<AiGuidelines>(), Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("add"));

        var parameters = new Dictionary<string, object>
        {
            { "guidelines", "- Rule" }
        };

        await _skill.ExecuteAsync(_context, parameters);

        callOrder.Should().ContainInOrder("deactivate", "add");
    }
}
