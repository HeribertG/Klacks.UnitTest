// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RunAnalysisSkill: it forwards the question and the caller's context to the read-only
/// research service and surfaces the synthesis as the skill message (so only the compact synthesis, not
/// the intermediate tool output, flows back to the outer turn), and it rejects a missing question.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class RunAnalysisSkillTests
{
    private const string QuestionParameter = "question";

    private IReadOnlyResearchService _researchService = null!;
    private RunAnalysisSkill _skill = null!;

    private static readonly Guid CallerUserId = Guid.NewGuid();

    private static SkillExecutionContext Context() => new()
    {
        UserId = CallerUserId,
        TenantId = Guid.Empty,
        UserName = "caller",
        UserPermissions = new List<string>()
    };

    [SetUp]
    public void SetUp()
    {
        _researchService = Substitute.For<IReadOnlyResearchService>();
        _skill = new RunAnalysisSkill(_researchService);
    }

    [Test]
    public async Task ExecuteAsync_ReturnsSynthesisAsMessage_AndForwardsQuestionAndContext()
    {
        _researchService.ResearchAsync("analyze July", Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReadOnlyResearchResult(
                "The roster has two gaps in week 30.", 2, 3,
                new List<string> { "check_absence_conflicts" }, ModelAvailable: true));

        var context = Context();
        var result = await _skill.ExecuteAsync(
            context, new Dictionary<string, object> { [QuestionParameter] = "analyze July" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("The roster has two gaps in week 30.");
        result.Data.ShouldNotBeNull();

        await _researchService.Received(1).ResearchAsync(
            "analyze July",
            Arg.Is<SkillExecutionContext>(c => c.UserId == CallerUserId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void ExecuteAsync_MissingQuestion_Throws()
    {
        Should.ThrowAsync<ArgumentException>(() =>
            _skill.ExecuteAsync(Context(), new Dictionary<string, object>()));
    }
}
