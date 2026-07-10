// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the navigation-training skills — list_navigation_targets (status
/// aggregation, obsolete filtering), update_navigation_synonyms (additive merge,
/// needs-review status, unknown target, verify failure) and list_navigation_feedback.
/// </summary>

using Klacks.Api.Application.Handlers.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Klacksy;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NavigationTrainingSkillTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static NavigationTarget Target(string id, string status = "reviewed",
        bool obsolete = false, params string[] deSynonyms) => new()
    {
        TargetId = id,
        Route = $"/workplace/{id}",
        LabelKey = $"nav.{id}",
        SynonymStatus = status,
        Obsolete = obsolete,
        Synonyms = deSynonyms.Length > 0
            ? new Dictionary<string, string[]> { ["de"] = deSynonyms }
            : new Dictionary<string, string[]>()
    };

    [Test]
    public async Task ListTargets_AggregatesStatuses_AndSkipsObsolete()
    {
        _mediator.Send(Arg.Any<GetNavigationTargetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<NavigationTarget>
            {
                Target("dashboard", status: "reviewed"),
                Target("inbox", status: "pending"),
                Target("old-page", status: "pending", obsolete: true)
            });
        var skill = new ListNavigationTargetsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2 navigation target(s)");
        result.Message.ShouldContain("1 pending");
        result.Message.ShouldContain("1 reviewed");
    }

    [Test]
    public async Task UpdateSynonyms_MergesAdditively_AndSetsNeedsReview()
    {
        var target = Target("dashboard", deSynonyms: new[] { "übersicht" });
        _mediator.Send(Arg.Any<GetNavigationTargetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<NavigationTarget> { target },
                new List<NavigationTarget>
                {
                    Target("dashboard", status: "needs-review",
                        deSynonyms: new[] { "übersicht", "lagebild", "cockpit" })
                });
        _mediator.Send(Arg.Any<UpdateNavigationTargetSynonymsCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new UpdateNavigationSynonymsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["targetId"] = "dashboard",
            ["locale"] = "DE",
            ["synonyms"] = "Lagebild, Cockpit, übersicht"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Added 2 synonym(s)");
        result.Message.ShouldContain("needs-review");
        await _mediator.Received(1).Send(
            Arg.Is<UpdateNavigationTargetSynonymsCommand>(c =>
                c.TargetId == "dashboard"
                && c.Locale == "de"
                && c.Status == "needs-review"
                && c.Synonyms.Contains("übersicht")
                && c.Synonyms.Contains("lagebild")
                && c.Synonyms.Contains("cockpit")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateSynonyms_NoOp_WhenAllPhrasesAlreadyExist()
    {
        _mediator.Send(Arg.Any<GetNavigationTargetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<NavigationTarget>
            {
                Target("dashboard", deSynonyms: new[] { "übersicht" })
            });
        var skill = new UpdateNavigationSynonymsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["targetId"] = "dashboard",
            ["locale"] = "de",
            ["synonyms"] = "Übersicht"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("nothing to add");
        await _mediator.DidNotReceive().Send(
            Arg.Any<UpdateNavigationTargetSynonymsCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateSynonyms_ReturnsError_ForUnknownTarget()
    {
        _mediator.Send(Arg.Any<GetNavigationTargetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<NavigationTarget> { Target("dashboard") });
        var skill = new UpdateNavigationSynonymsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["targetId"] = "does-not-exist",
            ["locale"] = "de",
            ["synonyms"] = "irgendwas"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        result.Message.ShouldContain("dashboard");
    }

    [Test]
    public async Task UpdateSynonyms_ReturnsError_WhenVerificationMissesNewPhrase()
    {
        var target = Target("dashboard", deSynonyms: new[] { "übersicht" });
        _mediator.Send(Arg.Any<GetNavigationTargetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<NavigationTarget> { target },
                new List<NavigationTarget> { Target("dashboard", deSynonyms: new[] { "übersicht" }) });
        _mediator.Send(Arg.Any<UpdateNavigationTargetSynonymsCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new UpdateNavigationSynonymsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["targetId"] = "dashboard",
            ["locale"] = "de",
            ["synonyms"] = "lagebild"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task ListFeedback_ListsUnresolvedUtterances()
    {
        _mediator.Send(Arg.Any<GetNavigationFeedbackQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<KlacksyNavigationFeedback>
            {
                new() { Utterance = "geh zur mannschaftsseite", Locale = "de", UserAction = "none", Timestamp = new DateTime(2026, 7, 9, 10, 0, 0) }
            });
        var skill = new ListNavigationFeedbackSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["locale"] = "de"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("1 unresolved navigation utterance(s)");
        result.Message.ShouldContain("update_navigation_synonyms");
    }
}
