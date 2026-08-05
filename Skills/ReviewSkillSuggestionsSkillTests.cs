// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ReviewSkillSuggestionsSkill — the accept path must persist the new skill and
/// refresh the skill catalogue (cache, registry, knowledge index) so retrieval sees it immediately.
/// </summary>
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ReviewSkillSuggestionsSkillTests
{
    private const string AcceptAction = "accept";

    private ISkillGapRepository _skillGapRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IAgentSkillRepository _agentSkillRepository = null!;
    private ISkillPhraseRepository _skillPhraseRepository = null!;
    private ISkillCatalogRefresher _skillCatalogRefresher = null!;
    private ReviewSkillSuggestionsSkill _skill = null!;
    private SkillExecutionContext _context = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _skillGapRepository = Substitute.For<ISkillGapRepository>();
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentSkillRepository = Substitute.For<IAgentSkillRepository>();
        _skillPhraseRepository = Substitute.For<ISkillPhraseRepository>();
        _skillCatalogRefresher = Substitute.For<ISkillCatalogRefresher>();

        _skill = new ReviewSkillSuggestionsSkill(
            _skillGapRepository, _agentRepository, _agentSkillRepository,
            _skillPhraseRepository, _skillCatalogRefresher);

        _agent = new Agent { Id = Guid.NewGuid() };
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _agentSkillRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns([]);

        _context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.Empty,
            UserName = "admin",
            UserPermissions = new[] { "Admin" }
        };
    }

    [Test]
    public async Task ExecuteAsync_AcceptSuggestion_PersistsSkillAndRefreshesCatalog()
    {
        var gap = new SkillGapRecord
        {
            Id = Guid.NewGuid(),
            DetectedIntent = "export shifts to csv",
            SuggestedSkillName = "export_shifts_csv",
            SuggestedDescription = "Exports shifts as CSV.",
            Status = SkillGapStatuses.Suggested
        };
        _skillGapRepository.GetPendingAsync(_agent.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([gap]);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            ["action"] = AcceptAction,
            ["gapId"] = gap.Id.ToString()
        });

        result.Success.ShouldBeTrue();
        gap.Status.ShouldBe(SkillGapStatuses.Accepted);
        await _agentSkillRepository.Received(1).AddAsync(
            Arg.Is<AgentSkill>(s => s.Name == "export_shifts_csv"), Arg.Any<CancellationToken>());
        await _skillCatalogRefresher.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_AcceptUnknownGap_DoesNotRefreshCatalog()
    {
        _skillGapRepository.GetPendingAsync(_agent.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _skill.ExecuteAsync(_context, new Dictionary<string, object>
        {
            ["action"] = AcceptAction,
            ["gapId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await _agentSkillRepository.DidNotReceive().AddAsync(Arg.Any<AgentSkill>(), Arg.Any<CancellationToken>());
        await _skillCatalogRefresher.DidNotReceive().RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
