// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the planning-profile setup skills: start creates a draft and returns the checklist,
/// set validates and stores only valid parameters, preview summarises the base choice and the rules that
/// would be created (including the empty-industry warning), apply relays the mediator outcome and cancel
/// clears the draft. Draft-store interactions use the real persistent store (backed by an EF InMemory
/// database) and the real catalog/validator; persistence of the applied profile is dispatched through a
/// substituted mediator, and the industry template lookup through a substituted repository.
/// </summary>

using Klacks.Api.Application.Commands.PlanningProfile;
using Klacks.Api.Application.DTOs.PlanningProfile;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills.PlanningProfile;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PlanningProfileSkillTests
{
    private IPendingPlanningProfileDraftStore _store = null!;
    private PlanningProfileParameterCatalog _catalog = null!;
    private PlanningProfileDraftValidator _validator = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string Key = "planning-profile-setup";

    [SetUp]
    public void Setup()
    {
        _store = PendingStoreTestFactory.CreatePlanningProfileDraftStore();
        _catalog = new PlanningProfileParameterCatalog();
        _validator = new PlanningProfileDraftValidator(_catalog);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private PlanningProfileDraft? StoredDraft() => _store.Get(UserId, Key);

    [Test]
    public async Task Start_CreatesDraft_ReturnsChecklist_AsksBaseIndustryFirst()
    {
        var skill = new StartPlanningProfileSetupSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        StoredDraft().ShouldNotBeNull();
        result.Message.ShouldContain(PlanningProfileParameterNames.BaseIndustry);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain(PlanningProfileParameterNames.BaseIndustry);
    }

    [Test]
    public async Task Set_StoresValidValue_RejectsInvalid()
    {
        _store.Set(UserId, Key, new PlanningProfileDraft());
        var skill = new SetPlanningProfileParametersSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["parameters"] = "{\"baseIndustry\":\"homecare\",\"maxWeeklyHours\":\"not-a-number\"}"
        });

        result.Success.ShouldBeTrue();
        var draft = StoredDraft();
        draft!.Parameters.ContainsKey(PlanningProfileParameterNames.BaseIndustry).ShouldBeTrue();
        draft.Parameters.ContainsKey(PlanningProfileParameterNames.MaxWeeklyHours).ShouldBeFalse();
    }

    [Test]
    public async Task Set_NoDraft_ReturnsError()
    {
        var skill = new SetPlanningProfileParametersSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["parameters"] = "{\"baseIndustry\":\"homecare\"}"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Preview_MissingBaseIndustry_ReturnsChecklistNotPreview()
    {
        _store.Set(UserId, Key, new PlanningProfileDraft());
        var skill = new PreviewPlanningProfileSkill(_store, _catalog, _validator, Substitute.For<ISchedulingRuleRepository>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("missing");
    }

    [Test]
    public async Task Preview_Industry_ListsRulesThatWillBeCopied()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = IndustrySlugs.Homecare;
        _store.Set(UserId, Key, draft);

        var repo = Substitute.For<ISchedulingRuleRepository>();
        repo.GetByIndustryAsync(IndustrySlugs.Homecare).Returns(new List<SchedulingRule>
        {
            new() { Name = "Homecare day", Industry = IndustrySlugs.Homecare },
            new() { Name = "Homecare night", Industry = IndustrySlugs.Homecare }
        });
        var skill = new PreviewPlanningProfileSkill(_store, _catalog, _validator, repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("Homecare day");
        json.ShouldContain(IndustrySlugs.Custom);
    }

    [Test]
    public async Task Preview_Industry_NoTemplates_WarnsNothingWouldBeCreated()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = IndustrySlugs.Security;
        _store.Set(UserId, Key, draft);

        var repo = Substitute.For<ISchedulingRuleRepository>();
        repo.GetByIndustryAsync(IndustrySlugs.Security).Returns(new List<SchedulingRule>());
        var skill = new PreviewPlanningProfileSkill(_store, _catalog, _validator, repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no template");
    }

    [Test]
    public async Task Preview_Scratch_WithoutOverride_ReturnsChecklist()
    {
        var draft = new PlanningProfileDraft();
        draft.Parameters[PlanningProfileParameterNames.BaseIndustry] = PlanningProfileBaseChoices.Scratch;
        _store.Set(UserId, Key, draft);
        var skill = new PreviewPlanningProfileSkill(_store, _catalog, _validator, Substitute.For<ISchedulingRuleRepository>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("missing");
    }

    [Test]
    public async Task Apply_RelaysValidationError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ApplyPlanningProfileCommand>(), Arg.Any<CancellationToken>())
            .Returns<PlanningProfileApplyResult?>(_ => throw new InvalidRequestException("still missing required: baseIndustry"));
        var skill = new ApplyPlanningProfileSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("still missing required: baseIndustry");
    }

    [Test]
    public async Task Apply_Success_ReturnsRuleSummary()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ApplyPlanningProfileCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PlanningProfileApplyResult
            {
                BaseChoice = IndustrySlugs.Homecare,
                CreatedRuleCount = 2,
                CreatedRuleNames = new List<string> { "Homecare day", "Homecare night" },
                ActiveIndustries = IndustrySlugs.Custom
            });
        var skill = new ApplyPlanningProfileSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Homecare day");
        result.Message.ShouldContain(IndustrySlugs.Custom);
    }

    [Test]
    public async Task Cancel_ClearsDraft()
    {
        _store.Set(UserId, Key, new PlanningProfileDraft());
        var skill = new CancelPlanningProfileSetupSkill(_store);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        StoredDraft().ShouldBeNull();
    }
}
