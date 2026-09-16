// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The rollback proposal reads the same table the correction undo reads, so the structured entries added
/// for the undo must not change what it answers. An UndoOnly entry is therefore ABSENT as far as this
/// skill is concerned - the skill answers "not in the rollback whitelist", exactly as before those
/// entries existed - while a manual entry and a full entry keep their own two answers.
/// </summary>

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RollbackMyLastChangeSkillTests
{
    private const string UndoOnlySkill = "add_shift_to_group";
    private const string ManualSkill = "create_shift";
    private const string FullyMappedSkill = "place_work";
    private const string InverseOfFullyMappedSkill = "delete_work";
    private const string NotWhitelistedMarker = "not in the rollback whitelist";
    private const string NoAutomaticPathMarker = "no automatic rollback path";

    private IAgentSkillExecutionRepository _executionRepository = null!;
    private RollbackMyLastChangeSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _executionRepository = Substitute.For<IAgentSkillExecutionRepository>();
        _skill = new RollbackMyLastChangeSkill(_executionRepository);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private void GivenTheLastExecutionWas(string toolName) =>
        _executionRepository
            .GetLastSuccessfulForUserAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new AgentSkillExecution
            {
                Id = Guid.NewGuid(),
                ToolName = toolName,
                ParametersJson = """{"shiftId":"s-1","groupId":"g-1"}""",
                ResultMessage = "done",
                Success = true
            });

    private Task<SkillResult> Execute() =>
        _skill.ExecuteAsync(Context(), new Dictionary<string, object>(), CancellationToken.None);

    [Test]
    public async Task WithoutARecentExecution_ItSaysSo()
    {
        var result = await Execute();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No successful skill execution");
    }

    [Test]
    public async Task AnUndoOnlyEntry_IsTreatedAsAbsentFromTheWhitelist()
    {
        GivenTheLastExecutionWas(UndoOnlySkill);

        var result = await Execute();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(NotWhitelistedMarker);
        result.Message.ShouldNotContain(NoAutomaticPathMarker);
    }

    [Test]
    public async Task AManualEntry_KeepsItsOwnAnswer()
    {
        GivenTheLastExecutionWas(ManualSkill);

        var result = await Execute();

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(NoAutomaticPathMarker);
    }

    [Test]
    public async Task AFullyMappedEntry_ProposesTheInverseSkill()
    {
        GivenTheLastExecutionWas(FullyMappedSkill);

        var result = await Execute();

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(InverseOfFullyMappedSkill);
        result.Data.ShouldNotBeNull();
    }
}
