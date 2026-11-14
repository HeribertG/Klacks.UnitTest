// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The in-turn tool search: find_tools splits its needs (at most ToolSearchConstants.MaxNeedsPerCall), looks each one
/// up in the skill index restricted to the skills the user may use that are not always-on and not find_tools itself,
/// and offers what it found to the turn as ToolSearch functions. A hit outside that set is never offered.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class FindToolsSkillTests
{
    private const string FreeSkill = "assign_contract";
    private const string PermittedSkill = "add_clients_to_group";
    private const string ForbiddenSkill = "delete_client";
    private const string AlwaysOnSkill = "navigate_to";
    private const string HeldPermission = Permissions.CanEditGroups;
    private const string MissingPermission = Permissions.CanDeleteClients;

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private ITurnToolsetExtension _toolsetExtension = null!;
    private FindToolsSkill _skill = null!;
    private Agent _agent = null!;
    private Guid _turnId;
    private List<IReadOnlyList<LLMFunction>> _offers = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid(), Name = "Klacksy" };
        _turnId = Guid.NewGuid();
        _offers = [];

        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                CreateSkill(FreeSkill),
                CreateSkill(PermittedSkill, requiredPermission: HeldPermission),
                CreateSkill(ForbiddenSkill, requiredPermission: MissingPermission),
                CreateSkill(AlwaysOnSkill, alwaysOn: true),
                CreateSkill(ToolSearchConstants.SkillName)
            });

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        GivenRetrievalReturns();

        _toolsetExtension = Substitute.For<ITurnToolsetExtension>();
        _toolsetExtension.Offer(Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<LLMFunction>>())
            .Returns(call =>
            {
                var functions = call.ArgAt<IReadOnlyList<LLMFunction>>(1);
                _offers.Add(functions);
                return functions;
            });

        _skill = new FindToolsSkill(_skillCache, _retrieval, _toolsetExtension);
    }

    private SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "planner",
        UserPermissions = new List<string> { HeldPermission },
        TurnId = _turnId
    };

    private static AgentSkill CreateSkill(string name, bool alwaysOn = false, string? requiredPermission = null) => new()
    {
        Name = name,
        Description = $"{name} does something.",
        ParametersJson = "[{\"name\":\"clientId\",\"type\":\"string\",\"description\":\"The client\",\"required\":true}]",
        AlwaysOn = alwaysOn,
        RequiredPermission = requiredPermission
    };

    private void GivenRetrievalReturns(params string[] sourceIds) =>
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(Hits(sourceIds));

    private static RetrievalResult Hits(params string[] sourceIds) =>
        new(sourceIds
            .Select(id => new RetrievalCandidate(
                new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = id, Text = id },
                0.9))
            .ToList());

    private Task<SkillResult> Execute(string? needs)
    {
        var parameters = new Dictionary<string, object>();
        if (needs != null)
        {
            parameters[ToolSearchConstants.NeedsParameter] = needs;
        }

        return _skill.ExecuteAsync(Context(), parameters);
    }

    private static List<string> ToolNames(SkillResult result)
    {
        var tools = (System.Collections.IEnumerable)result.Data!.GetType().GetProperty("tools")!.GetValue(result.Data)!;
        return tools.Cast<object>().Select(t => (string)t.GetType().GetProperty("name")!.GetValue(t)!).ToList();
    }

    private List<string> OfferedNames() => _offers.SelectMany(o => o).Select(f => f.Name).ToList();

    [Test]
    public async Task Needs_AreSplitTrimmedAndCappedAtTheMaximumPerCall()
    {
        await Execute(" first need ;; second need; third need ; fourth need");

        await _retrieval.Received(ToolSearchConstants.MaxNeedsPerCall).RetrieveAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
            Arg.Any<IReadOnlyCollection<string>?>());
        foreach (var need in new[] { "first need", "second need", "third need" })
        {
            await _retrieval.Received(1).RetrieveAsync(
                need, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>());
        }

        await _retrieval.DidNotReceive().RetrieveAsync(
            "fourth need", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
            Arg.Any<IReadOnlyCollection<string>?>());
    }

    [Test]
    public async Task TheSearch_IsRestrictedToPermittedNonAlwaysOnSkillsOtherThanItself()
    {
        IReadOnlyCollection<string>? sourceIdFilter = null;
        KnowledgeEntryKind? kindFilter = null;
        int? topK = null;
        bool? isAdmin = null;
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Do<bool>(a => isAdmin = a),
                Arg.Do<int>(k => topK = k), Arg.Any<string?>(), Arg.Any<CancellationToken>(),
                Arg.Do<KnowledgeEntryKind?>(k => kindFilter = k),
                Arg.Do<IReadOnlyCollection<string>?>(f => sourceIdFilter = f))
            .Returns(Hits());

        await Execute("assign a contract");

        sourceIdFilter.ShouldNotBeNull();
        sourceIdFilter!.OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(new[] { PermittedSkill, FreeSkill }.OrderBy(n => n, StringComparer.Ordinal));
        kindFilter.ShouldBe(KnowledgeEntryKind.Skill);
        topK.ShouldBe(ToolSearchConstants.HitsPerNeed);
        isAdmin.ShouldBe(false);
    }

    [Test]
    public async Task FoundSkills_AreOfferedToTheTurnAsToolSearchFunctions()
    {
        GivenRetrievalReturns(FreeSkill, PermittedSkill);

        var result = await Execute("assign a contract");

        result.Success.ShouldBeTrue();
        ToolNames(result).ShouldBe(new[] { FreeSkill, PermittedSkill });
        _toolsetExtension.Received(1).Offer(_turnId, Arg.Any<IReadOnlyList<LLMFunction>>());
        var offered = _offers.Single();
        offered.Select(f => f.Name).ShouldBe(new[] { FreeSkill, PermittedSkill });
        offered.ShouldAllBe(f => f.ToolsetSource == ToolsetSkillSource.ToolSearch);
        offered[0].Description.ShouldBe($"{FreeSkill} does something.");
        offered[0].Parameters.ShouldContainKey("clientId");
        offered[0].RequiredParameters.ShouldBe(new[] { "clientId" });
    }

    [Test]
    public async Task OnlyTheBestHitsPerNeedAreKept_AndAHitOfTwoNeedsIsOfferedOnce()
    {
        _retrieval.RetrieveAsync(
                "first need", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(Hits(FreeSkill, FreeSkill, FreeSkill, PermittedSkill));
        _retrieval.RetrieveAsync(
                "second need", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(Hits(FreeSkill));

        await Execute("first need; second need");

        OfferedNames().ShouldBe(new[] { FreeSkill });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ; ;; ")]
    public async Task EmptyNeeds_AreAnErrorAndSearchNothing(string? needs)
    {
        var result = await Execute(needs);

        result.Success.ShouldBeFalse();
        await _retrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default, default);
        _toolsetExtension.DidNotReceiveWithAnyArgs().Offer(default, default!);
    }

    [Test]
    public async Task NoMatchingSkill_IsASuccessWithNoTools()
    {
        var result = await Execute("something nobody offers");

        result.Success.ShouldBeTrue();
        ToolNames(result).ShouldBeEmpty();
        OfferedNames().ShouldBeEmpty();
    }

    [Test]
    public async Task HitsOutsideTheSearchableSet_AreNeverOffered()
    {
        GivenRetrievalReturns(ForbiddenSkill, AlwaysOnSkill, ToolSearchConstants.SkillName, "unknown_skill");

        var result = await Execute("delete a client");

        result.Success.ShouldBeTrue();
        ToolNames(result).ShouldBeEmpty();
        OfferedNames().ShouldBeEmpty();
    }
}
