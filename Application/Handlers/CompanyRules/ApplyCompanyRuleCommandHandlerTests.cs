// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for <see cref="ApplyCompanyRuleCommandHandler"/> covering each rule kind: surcharge writes
/// a snapshot and the new values, counter rules resolve the optional scheduling-rule scope and reject an
/// ambiguous or unknown name, and custom macros reject a name collision. An incomplete draft missing a
/// required parameter is rejected before anything is persisted. Uses the real persistent draft store
/// (backed by an EF InMemory database), catalog and validator with a substituted settings repository,
/// company-rule repository and mediator.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.CompanyRules;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.CompanyRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using MacroCommands = Klacks.Api.Application.Commands.Settings.Macros;
using MacroQueries = Klacks.Api.Application.Queries.Settings.Macros;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Handlers.CompanyRules;

[TestFixture]
public class ApplyCompanyRuleCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Key = "conv-1";

    private IPendingCompanyRuleDraftStore _store = null!;
    private CompanyRuleParameterCatalog _catalog = null!;
    private CompanyRuleDraftValidator _validator = null!;
    private ISettingsRepository _settings = null!;
    private ICompanyRuleRepository _registry = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Klacks.Api.Domain.Events.IDomainEventDispatcher _eventDispatcher = null!;
    private ApplyCompanyRuleCommandHandler _sut = null!;
    private CompanyRule? _added;

    [SetUp]
    public void Setup()
    {
        _store = PendingStoreTestFactory.CreateCompanyRuleDraftStore();
        _catalog = new CompanyRuleParameterCatalog();
        _validator = new CompanyRuleDraftValidator(_catalog);
        _settings = Substitute.For<ISettingsRepository>();
        _settings
            .UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(async callInfo =>
            {
                var type = callInfo.ArgAt<string>(0);
                var value = callInfo.ArgAt<string>(1);
                var existing = await _settings.GetSetting(type);
                if (existing is not null)
                {
                    existing.Value = value;
                    await _settings.PutSetting(existing);
                }
                else
                {
                    await _settings.AddSetting(new SettingsEntity { Id = Guid.NewGuid(), Type = type, Value = value });
                }
            });
        _registry = Substitute.For<ICompanyRuleRepository>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _added = null;
        _registry.When(r => r.Add(Arg.Any<CompanyRule>())).Do(ci => _added = ci.Arg<CompanyRule>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<CompanyRule>>>())
            .Returns(ci => ci.ArgAt<Func<Task<CompanyRule>>>(0)());

        _eventDispatcher = Substitute.For<Klacks.Api.Domain.Events.IDomainEventDispatcher>();

        _sut = new ApplyCompanyRuleCommandHandler(
            _store, _validator, _catalog, _settings, _registry, _mediator, _unitOfWork, new SettingsMapper(),
            _eventDispatcher, Substitute.For<Microsoft.Extensions.Logging.ILogger<ApplyCompanyRuleCommandHandler>>());
    }

    private static ApplyCompanyRuleCommand Cmd() => new(UserId, Key);

    [Test]
    public void Handle_NoDraft_Throws()
    {
        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
    }

    [Test]
    public void Handle_IncompleteDraft_MissingRequiredParameter_Throws_NothingPersisted()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        _store.Set(UserId, Key, draft);

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));

        _added.ShouldBeNull();
        _registry.DidNotReceive().Add(Arg.Any<CompanyRule>());
        _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<CompanyRule>>>());
        _store.Get(UserId, Key).ShouldNotBeNull();
    }

    [Test]
    public async Task Handle_Surcharge_SnapshotsOldValues_AndWritesNew()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings, RuleText = "night pay", Name = "Night pay" };
        draft.Parameters[CompanyRuleParameterNames.NightRate] = "1.5";
        _store.Set(UserId, Key, draft);

        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.25" });

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Kind.ShouldBe(CompanyRuleKind.SurchargeSettings);
        result.TargetEntityType.ShouldBe(CompanyRuleTargetEntityTypes.Settings);
        result.TargetEntityId.ShouldBeNull();

        _added.ShouldNotBeNull();
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, string?>>(_added!.SettingsSnapshotJson!);
        snapshot!.ShouldContainKey(SettingKeys.NightRate);
        snapshot[SettingKeys.NightRate].ShouldBe("1.25");
        await _settings.Received().PutSetting(Arg.Is<SettingsEntity>(s => s.Type == SettingKeys.NightRate && s.Value == "1.5"));
        _store.Get(UserId, Key).ShouldBeNull();
    }

    [Test]
    public async Task Handle_Surcharge_ChangedRelevantValue_DispatchesSurchargeSettingsChangedEvent()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings, RuleText = "night pay" };
        draft.Parameters[CompanyRuleParameterNames.NightRate] = "1.5";
        _store.Set(UserId, Key, draft);

        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.25" });

        await _sut.Handle(Cmd(), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<Klacks.Api.Domain.Events.SurchargeSettingsChangedEvent>(e =>
                e.ChangedKeys.Count == 1 && e.ChangedKeys.Contains(SettingKeys.NightRate)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Surcharge_UnchangedValue_DoesNotDispatch()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.SurchargeSettings, RuleText = "night pay" };
        draft.Parameters[CompanyRuleParameterNames.NightRate] = "1.5";
        _store.Set(UserId, Key, draft);

        _settings.GetSetting(SettingKeys.NightRate).Returns(new SettingsEntity { Id = Guid.NewGuid(), Type = SettingKeys.NightRate, Value = "1.5" });

        await _sut.Handle(Cmd(), CancellationToken.None);

        await _eventDispatcher.DidNotReceiveWithAnyArgs().DispatchAsync((Klacks.Api.Domain.Events.IDomainEvent)null!, default);
    }

    [Test]
    public async Task Handle_CounterRule_CreatesRule_TargetIsNewId()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "warn";
        _store.Set(UserId, Key, draft);

        var createdId = Guid.NewGuid();
        _mediator.Send(Arg.Any<PostCommand<CounterRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new CounterRuleResource { Id = createdId, EventType = CounterEventType.NightShift, Period = CounterPeriod.Year, Threshold = 25 });

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.TargetEntityType.ShouldBe(CompanyRuleTargetEntityTypes.CounterRule);
        result.TargetEntityId.ShouldBe(createdId);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<CounterRuleResource>>(c =>
                c.Resource.EventType == CounterEventType.NightShift &&
                c.Resource.Period == CounterPeriod.Year &&
                c.Resource.Threshold == 25 &&
                c.Resource.Enforcement == RuleEnforcementMode.Warn &&
                c.Resource.SchedulingRuleId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CounterRule_MapsBlockEnforcementOntoRule()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "block";
        _store.Set(UserId, Key, draft);

        _mediator.Send(Arg.Any<PostCommand<CounterRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new CounterRuleResource { Id = Guid.NewGuid(), EventType = CounterEventType.NightShift, Period = CounterPeriod.Year, Threshold = 25 });

        await _sut.Handle(Cmd(), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<CounterRuleResource>>(c => c.Resource.Enforcement == RuleEnforcementMode.Block),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Handle_CounterRule_AmbiguousSchedulingRule_Throws_NoPost()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "warn";
        draft.Parameters[CompanyRuleParameterNames.SchedulingRuleName] = "Care";
        _store.Set(UserId, Key, draft);

        _mediator.Send(Arg.Any<Klacks.Api.Application.Queries.ListQuery<SchedulingRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingRuleResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Care Day" },
                new() { Id = Guid.NewGuid(), Name = "Care Night" }
            });

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
    }

    [Test]
    public void Handle_CounterRule_UnknownSchedulingRule_Throws()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CounterRule, RuleText = "25 nights" };
        draft.Parameters[CompanyRuleParameterNames.EventType] = "NightShift";
        draft.Parameters[CompanyRuleParameterNames.Period] = "Year";
        draft.Parameters[CompanyRuleParameterNames.Threshold] = "25";
        draft.Parameters[CompanyRuleParameterNames.Enforcement] = "warn";
        draft.Parameters[CompanyRuleParameterNames.SchedulingRuleName] = "Nonexistent";
        _store.Set(UserId, Key, draft);

        _mediator.Send(Arg.Any<Klacks.Api.Application.Queries.ListQuery<SchedulingRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchedulingRuleResource> { new() { Id = Guid.NewGuid(), Name = "Care Day" } });

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
    }

    [Test]
    public async Task Handle_CustomMacro_Creates_TargetIsNewId()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CustomMacro, RuleText = "holiday pay" };
        draft.Parameters[CompanyRuleParameterNames.MacroName] = "HolidayPay";
        draft.Parameters[CompanyRuleParameterNames.MacroScript] = "OUTPUT 14, 1";
        _store.Set(UserId, Key, draft);

        var createdId = Guid.NewGuid();
        _mediator.Send(Arg.Any<MacroQueries.ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>());
        _mediator.Send(Arg.Any<MacroCommands.PostCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = createdId, Name = "HolidayPay" });

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result!.TargetEntityType.ShouldBe(CompanyRuleTargetEntityTypes.Macro);
        result.TargetEntityId.ShouldBe(createdId);
    }

    [Test]
    public void Handle_CustomMacro_NameCollision_Throws_NoPost()
    {
        var draft = new CompanyRuleDraft { Kind = CompanyRuleKind.CustomMacro, RuleText = "holiday pay" };
        draft.Parameters[CompanyRuleParameterNames.MacroName] = "HolidayPay";
        draft.Parameters[CompanyRuleParameterNames.MacroScript] = "OUTPUT 14, 1";
        _store.Set(UserId, Key, draft);

        _mediator.Send(Arg.Any<MacroQueries.ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource> { new() { Id = Guid.NewGuid(), Name = "holidaypay" } });

        Assert.ThrowsAsync<InvalidRequestException>(() => _sut.Handle(Cmd(), CancellationToken.None));
    }
}
