// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the company-rule intake skills: start creates a draft and returns the checklist,
/// set validates and stores only valid parameters, preview summarises the draft, apply/revert relay the
/// mediator outcome, cancel clears the draft and list projects the registry. Draft-store interactions use
/// the real in-memory store and the real catalog/validator; persistence is dispatched through a
/// substituted mediator.
/// </summary>

using Klacks.Api.Application.Commands.CompanyRules;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills.CompanyRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CompanyRuleSkillTests
{
    private InMemoryPendingCompanyRuleDraftStore _store = null!;
    private CompanyRuleParameterCatalog _catalog = null!;
    private CompanyRuleDraftValidator _validator = null!;

    private static readonly Guid UserId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _store = new InMemoryPendingCompanyRuleDraftStore();
        _catalog = new CompanyRuleParameterCatalog();
        _validator = new CompanyRuleDraftValidator(_catalog);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = UserId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private CompanyRuleDraft? StoredDraft()
        => _store.Get(UserId, "company-rule-intake");

    [Test]
    public async Task Start_CreatesDraft_ReturnsChecklist()
    {
        var skill = new StartCompanyRuleSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["kind"] = "counterRule",
            ["ruleText"] = "no more than 25 night shifts per year",
            ["name"] = "Night cap"
        });

        result.Success.ShouldBeTrue();
        var draft = StoredDraft();
        draft.ShouldNotBeNull();
        draft!.Kind.ShouldBe(CompanyRuleKind.CounterRule);
        draft.Name.ShouldBe("Night cap");
    }

    [Test]
    public async Task Start_CustomMacro_IncludesDslReference()
    {
        var skill = new StartCompanyRuleSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["kind"] = "customMacro",
            ["ruleText"] = "pay double on holidays"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("OUTPUT");
    }

    [Test]
    public async Task Start_UnknownKind_ReturnsError_NoDraft()
    {
        var skill = new StartCompanyRuleSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["kind"] = "nonsense",
            ["ruleText"] = "x"
        });

        result.Success.ShouldBeFalse();
        StoredDraft().ShouldBeNull();
    }

    [Test]
    public async Task Set_StoresValidValue_RejectsInvalid()
    {
        _store.Set(UserId, "company-rule-intake", new CompanyRuleDraft
        {
            Kind = CompanyRuleKind.CounterRule,
            RuleText = "cap"
        });
        var skill = new SetCompanyRuleParametersSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["parameters"] = "{\"threshold\":\"25\",\"period\":\"NotAPeriod\"}"
        });

        result.Success.ShouldBeTrue();
        var draft = StoredDraft();
        draft!.Parameters.ContainsKey(CompanyRuleParameterNames.Threshold).ShouldBeTrue();
        draft.Parameters.ContainsKey(CompanyRuleParameterNames.Period).ShouldBeFalse();
    }

    [Test]
    public async Task Set_NoDraft_ReturnsError()
    {
        var skill = new SetCompanyRuleParametersSkill(_store, _catalog, _validator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["parameters"] = "{\"threshold\":\"25\"}"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Preview_MissingRequired_ReturnsChecklistNotPreview()
    {
        _store.Set(UserId, "company-rule-intake", new CompanyRuleDraft
        {
            Kind = CompanyRuleKind.CounterRule,
            RuleText = "cap"
        });
        var skill = new PreviewCompanyRuleSkill(
            _store, _catalog, _validator,
            Substitute.For<ISettingsReader>(),
            Substitute.For<IMacroScriptValidator>(),
            Substitute.For<IComplianceEnforcementResolver>(),
            Substitute.For<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("missing");
    }

    [Test]
    public async Task Preview_CustomMacro_ShowsScriptValidation()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CustomMacro, RuleText = "x" };
        draft.Parameters[CompanyRuleParameterNames.MacroName] = "Holiday";
        draft.Parameters[CompanyRuleParameterNames.MacroScript] = "OUTPUT 1, 0";
        _store.Set(UserId, "company-rule-intake", draft);

        var macroValidator = Substitute.For<IMacroScriptValidator>();
        macroValidator.Validate(Arg.Any<string>()).Returns(MacroScriptValidationResult.Success());

        var skill = new PreviewCompanyRuleSkill(
            _store, _catalog, _validator,
            Substitute.For<ISettingsReader>(),
            macroValidator,
            Substitute.For<IComplianceEnforcementResolver>(),
            Substitute.For<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        macroValidator.Received(1).Validate("OUTPUT 1, 0");
    }

    [Test]
    public async Task Preview_CounterRule_EnforcementDivergesFromGlobal_ShowsOverrideHint()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "block";
        _store.Set(UserId, "company-rule-intake", draft);

        var resolver = Substitute.For<IComplianceEnforcementResolver>();
        resolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);

        var skill = new PreviewCompanyRuleSkill(
            _store, _catalog, _validator,
            Substitute.For<ISettingsReader>(),
            Substitute.For<IMacroScriptValidator>(),
            resolver,
            Substitute.For<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("Override active for this rule only");
    }

    [Test]
    public async Task Preview_CounterRule_EnforcementMatchesGlobal_ShowsNoOverride()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "warn";
        _store.Set(UserId, "company-rule-intake", draft);

        var resolver = Substitute.For<IComplianceEnforcementResolver>();
        resolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);

        var skill = new PreviewCompanyRuleSkill(
            _store, _catalog, _validator,
            Substitute.For<ISettingsReader>(),
            Substitute.For<IMacroScriptValidator>(),
            resolver,
            Substitute.For<IMediator>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("Matches the current global counter-rule mode");
        json.ShouldNotContain("Override active");
    }

    [Test]
    public async Task Apply_RelaysValidationError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ApplyCompanyRuleCommand>(), Arg.Any<CancellationToken>())
            .Returns<CompanyRuleResource?>(_ => throw new InvalidRequestException("missing required: threshold"));
        var skill = new ApplyCompanyRuleSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("missing required: threshold");
    }

    [Test]
    public async Task Apply_Success_ReturnsTargetReference()
    {
        var mediator = Substitute.For<IMediator>();
        var target = Guid.NewGuid();
        mediator.Send(Arg.Any<ApplyCompanyRuleCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CompanyRuleResource
            {
                Id = Guid.NewGuid(),
                Name = "Night cap",
                Kind = CompanyRuleKind.CounterRule,
                TargetEntityType = CompanyRuleTargetEntityTypes.CounterRule,
                TargetEntityId = target
            });
        var skill = new ApplyCompanyRuleSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Night cap");
    }

    [Test]
    public async Task Cancel_ClearsDraft()
    {
        _store.Set(UserId, "company-rule-intake", new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings, RuleText = "x" });
        var skill = new CancelCompanyRuleSkill(_store);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        StoredDraft().ShouldBeNull();
    }

    [Test]
    public async Task List_ProjectsRegistry()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<CompanyRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CompanyRuleResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Night cap", Kind = CompanyRuleKind.CounterRule }
            });
        var skill = new ListCompanyRulesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("1");
    }

    [Test]
    public async Task Revert_UnknownName_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<CompanyRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CompanyRuleResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Night cap", Kind = CompanyRuleKind.CounterRule }
            });
        var skill = new RevertCompanyRuleSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "totally different rule"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<RevertCompanyRuleCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Revert_ResolvedName_DispatchesRevert()
    {
        var mediator = Substitute.For<IMediator>();
        var id = Guid.NewGuid();
        mediator.Send(Arg.Any<ListQuery<CompanyRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CompanyRuleResource>
            {
                new() { Id = id, Name = "Night cap", Kind = CompanyRuleKind.CounterRule }
            });
        mediator.Send(Arg.Any<RevertCompanyRuleCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CompanyRuleResource { Id = id, Name = "Night cap", Kind = CompanyRuleKind.CounterRule });
        var skill = new RevertCompanyRuleSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Night cap"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(Arg.Is<RevertCompanyRuleCommand>(c => c.Id == id), Arg.Any<CancellationToken>());
    }
}
