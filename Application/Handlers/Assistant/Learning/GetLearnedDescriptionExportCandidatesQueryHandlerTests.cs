// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The export candidates are the gate-passed description proposals, newest first, each with the version
/// columns of its skill so the export report can name every skill whose database version ran ahead of its seed.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Application.Queries.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetLearnedDescriptionExportCandidatesQueryHandlerTests
{
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private GetLearnedDescriptionExportCandidatesQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _skills = Substitute.For<IAgentSkillRepository>();
        _handler = new GetLearnedDescriptionExportCandidatesQueryHandler(_proposals, _skills);
    }

    [Test]
    public async Task OnlyGatePassedDescriptionProposals_AreAskedFor()
    {
        _proposals.GetByStatusesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _handler.Handle(new GetLearnedDescriptionExportCandidatesQuery(7), CancellationToken.None);

        await _proposals.Received(1).GetByStatusesAsync(
            Arg.Is<IReadOnlyList<string>>(statuses => statuses.Count == 1 && statuses[0] == ProposedChangeStatuses.GatePassed),
            ProposedChangeFields.Description, 7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ACandidate_CarriesTheVersionsOfItsSkill()
    {
        var skill = new AgentSkill { Id = Guid.NewGuid(), Name = "list_clients", Description = "old", Version = 5, SeedVersion = 4 };
        var proposal = new ProposedSkillChange
        {
            Id = Guid.NewGuid(), SkillId = skill.Id, SkillName = skill.Name, ValueBefore = "old", ValueAfter = "new",
            Status = ProposedChangeStatuses.GatePassed, GateMetricsJson = "{\"verdict\":\"passed\"}", ReviewedAt = DateTime.UtcNow
        };
        _proposals.GetByStatusesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([proposal]);
        _skills.GetByIdAsync(skill.Id, Arg.Any<CancellationToken>()).Returns(skill);

        var candidate = (await _handler.Handle(new GetLearnedDescriptionExportCandidatesQuery(10), CancellationToken.None))
            .ShouldHaveSingleItem();

        candidate.ProposalId.ShouldBe(proposal.Id);
        candidate.SkillName.ShouldBe("list_clients");
        candidate.ValueBefore.ShouldBe("old");
        candidate.ValueAfter.ShouldBe("new");
        candidate.DbVersion.ShouldBe(5);
        candidate.DbSeedVersion.ShouldBe(4);
        candidate.DbDescription.ShouldBe("old");
        candidate.GateMetricsJson.ShouldBe("{\"verdict\":\"passed\"}");
    }

    [Test]
    public async Task ACandidateWhoseSkillIsGone_IsStillListedWithoutVersions()
    {
        _proposals.GetByStatusesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new ProposedSkillChange { Id = Guid.NewGuid(), SkillId = Guid.NewGuid(), SkillName = "gone", Status = ProposedChangeStatuses.GatePassed }]);

        var candidate = (await _handler.Handle(new GetLearnedDescriptionExportCandidatesQuery(10), CancellationToken.None))
            .ShouldHaveSingleItem();

        candidate.DbVersion.ShouldBeNull();
        candidate.DbDescription.ShouldBeNull();
    }
}
