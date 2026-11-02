// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the variant (key) management handlers: create validates name, group and period and refuses a hidden group;
/// update and delete answer a hidden group like a missing key and a consumed key with a conflict; delete refuses a key
/// an active scenario still carries and otherwise removes the key's data, its row and the links of finished scenarios
/// in that order; update refuses a key in use too; list reads the group with its visible subgroups and shift reads hide a
/// hidden group; IsExpired follows the company clock, not the server clock.
/// </summary>

using Klacks.Api.Application.Commands.ScenarioVariants;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ScenarioVariants;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ScenarioVariants;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ScenarioVariants;

[TestFixture]
public class ScenarioVariantHandlerTests
{
    private static readonly DateOnly From = new(2030, 3, 1);
    private static readonly DateOnly Until = new(2030, 3, 31);
    private static readonly DateOnly ClockToday = new(2030, 3, 15);

    private IScenarioVariantRepository _variantRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IGroupVisibilityGuard _guard = null!;
    private ICompanyClock _clock = null!;
    private Guid _visibleGroupId;
    private Guid _visibleSubgroupId;
    private Guid _hiddenGroupId;

    [SetUp]
    public void SetUp()
    {
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _visibleGroupId = Guid.NewGuid();
        _visibleSubgroupId = Guid.NewGuid();
        _hiddenGroupId = Guid.NewGuid();

        _guard = Substitute.For<IGroupVisibilityGuard>();
        _guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Guid>(0) == _visibleGroupId || call.ArgAt<Guid>(0) == _visibleSubgroupId);
        _scenarioService.GetGroupHierarchyIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new List<Guid> { call.ArgAt<Guid>(0) });

        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(ClockToday);
    }

    private AnalyseScenario Variant(
        Guid groupId,
        AnalyseScenarioStatus status = AnalyseScenarioStatus.Active,
        string name = "Winter keys",
        DateOnly? until = null)
    {
        var variant = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = name,
            GroupId = groupId,
            FromDate = From,
            UntilDate = until ?? Until,
            Kind = AnalyseScenarioKind.Variant,
            Status = status
        };
        _variantRepository.GetVariantAsync(variant.Id, Arg.Any<CancellationToken>()).Returns(variant);
        return variant;
    }

    private static Shift SealedShift(string name)
    {
        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = name,
            Status = ShiftStatus.SealedOrder,
            AnalyseToken = Guid.NewGuid()
        };
    }

    private CreateScenarioVariantCommandHandler CreateHandler()
    {
        return new CreateScenarioVariantCommandHandler(
            _scenarioRepository, _unitOfWork, _guard, _clock, Substitute.For<ILogger<CreateScenarioVariantCommandHandler>>());
    }

    private UpdateScenarioVariantCommandHandler UpdateHandler()
    {
        return new UpdateScenarioVariantCommandHandler(
            _variantRepository, _unitOfWork, _guard, _clock, Substitute.For<ILogger<UpdateScenarioVariantCommandHandler>>());
    }

    private DeleteScenarioVariantCommandHandler DeleteHandler()
    {
        return new DeleteScenarioVariantCommandHandler(
            _variantRepository, _scenarioRepository, _scenarioService, _unitOfWork, _guard,
            Substitute.For<ILogger<DeleteScenarioVariantCommandHandler>>());
    }

    private ListScenarioVariantsQueryHandler ListHandler()
    {
        return new ListScenarioVariantsQueryHandler(
            _variantRepository, _guard, _scenarioService, _clock, Substitute.For<ILogger<ListScenarioVariantsQueryHandler>>());
    }

    private GetScenarioVariantShiftsQueryHandler ShiftsHandler()
    {
        return new GetScenarioVariantShiftsQueryHandler(
            _variantRepository, _guard, new ScheduleMapper(), Substitute.For<ILogger<GetScenarioVariantShiftsQueryHandler>>());
    }

    private CreateScenarioVariantRequest CreateRequest(Guid groupId, string name = "Winter keys", DateOnly? until = null)
    {
        return new CreateScenarioVariantRequest
        {
            Name = name,
            Description = "Keys for the winter season",
            GroupId = groupId,
            FromDate = From,
            UntilDate = until ?? Until
        };
    }

    [Test]
    public async Task Create_WithoutGroup_IsRefusedAndNothingIsStored()
    {
        var command = new CreateScenarioVariantCommand(CreateRequest(Guid.Empty));

        await Should.ThrowAsync<InvalidRequestException>(
            () => CreateHandler().Handle(command, CancellationToken.None));

        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task Create_WithoutName_IsRefusedAndNothingIsStored(string? name)
    {
        var command = new CreateScenarioVariantCommand(CreateRequest(_visibleGroupId, name!));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => CreateHandler().Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantValidation.NameRequiredMessage);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Create_UntilBeforeFrom_IsRefusedAndNothingIsStored()
    {
        var command = new CreateScenarioVariantCommand(CreateRequest(_visibleGroupId, until: From.AddDays(-1)));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => CreateHandler().Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantValidation.PeriodInvalidMessage);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task Create_UntilEqualToFrom_IsAccepted()
    {
        var command = new CreateScenarioVariantCommand(CreateRequest(_visibleGroupId, until: From));

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.FromDate.ShouldBe(From);
        result.UntilDate.ShouldBe(From);
    }

    [Test]
    public async Task Create_HiddenGroup_IsAnsweredLikeAMissingGroupAndNothingIsStored()
    {
        var command = new CreateScenarioVariantCommand(CreateRequest(_hiddenGroupId));

        await Should.ThrowAsync<KeyNotFoundException>(
            () => CreateHandler().Handle(command, CancellationToken.None));

        await _scenarioRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Create_ValidRequest_StoresAnActiveVariantWithAFreshTokenAndReturnsIt()
    {
        AnalyseScenario? stored = null;
        await _scenarioRepository.Add(Arg.Do<AnalyseScenario>(variant => stored = variant));
        var command = new CreateScenarioVariantCommand(CreateRequest(_visibleGroupId));

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        stored.ShouldNotBeNull();
        stored.Kind.ShouldBe(AnalyseScenarioKind.Variant);
        stored.Token.ShouldNotBe(Guid.Empty);
        stored.Name.ShouldBe("Winter keys");
        stored.Description.ShouldBe("Keys for the winter season");
        stored.GroupId.ShouldBe(_visibleGroupId);
        stored.FromDate.ShouldBe(From);
        stored.UntilDate.ShouldBe(Until);
        stored.Status.ShouldBe(AnalyseScenarioStatus.Active);
        result.Token.ShouldBe(stored.Token);
        result.Id.ShouldBe(stored.Id);
        result.GroupId.ShouldBe(_visibleGroupId);
        result.Status.ShouldBe((int)AnalyseScenarioStatus.Active);
        result.ShiftCount.ShouldBe(0);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Update_ChangesNameDescriptionAndPeriodAndReturnsTheServiceCount()
    {
        var variant = Variant(_visibleGroupId);
        var newUntil = Until.AddDays(14);
        _variantRepository.CountShiftsByVariantTokenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(tokens => tokens.SequenceEqual(new[] { variant.Token })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [variant.Token] = 3 });
        var command = new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest
        {
            Name = "Spring keys",
            Description = "New description",
            FromDate = From.AddDays(1),
            UntilDate = newUntil
        });

        var result = await UpdateHandler().Handle(command, CancellationToken.None);

        variant.Name.ShouldBe("Spring keys");
        variant.Description.ShouldBe("New description");
        variant.FromDate.ShouldBe(From.AddDays(1));
        variant.UntilDate.ShouldBe(newUntil);
        result.Id.ShouldBe(variant.Id);
        result.Name.ShouldBe("Spring keys");
        result.UntilDate.ShouldBe(newUntil);
        result.ShiftCount.ShouldBe(3);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Update_ConsumedKey_ThrowsConflictAndChangesNothing()
    {
        var variant = Variant(_visibleGroupId, AnalyseScenarioStatus.Accepted);
        var command = new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest
        {
            Name = "Renamed",
            FromDate = From,
            UntilDate = Until
        });

        var exception = await Should.ThrowAsync<ConflictException>(
            () => UpdateHandler().Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.ConsumedMessage);
        variant.Name.ShouldBe("Winter keys");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_WhileAnActiveScenarioCarriesTheKey_ThrowsConflictAndChangesNothing()
    {
        var variant = Variant(_visibleGroupId);
        _variantRepository.HasActiveScenarioAsync(variant.Id, Arg.Any<CancellationToken>()).Returns(true);

        var exception = await Should.ThrowAsync<ConflictException>(() => UpdateHandler().Handle(
            new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest { Name = "Other", FromDate = From, UntilDate = Until }),
            CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.InUseMessage);
        variant.Name.ShouldBe("Winter keys");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_APeriodThatLeavesShiftsOutside_IsAConflictWithItsCodeAndChangesNothing()
    {
        var variant = Variant(_visibleGroupId);
        var shorter = Until.AddDays(-1);
        _variantRepository.HasShiftsOutsidePeriodAsync(variant.Token, From, shorter, Arg.Any<CancellationToken>()).Returns(true);

        var exception = await Should.ThrowAsync<ConflictException>(() => UpdateHandler().Handle(
            new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest { Name = "Other", FromDate = From, UntilDate = shorter }),
            CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantValidation.ShiftsOutsidePeriodMessage);
        exception.ConflictCode.ShouldBe(ScenarioVariantErrorCodes.ShiftsOutsidePeriod);
        variant.Name.ShouldBe("Winter keys");
        variant.UntilDate.ShouldBe(Until);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_OfTheNameOnly_DoesNotAskForShiftsOutsideThePeriod()
    {
        var variant = Variant(_visibleGroupId);
        _variantRepository.CountShiftsByVariantTokenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        await UpdateHandler().Handle(
            new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest { Name = "Other", FromDate = From, UntilDate = Until }),
            CancellationToken.None);

        variant.Name.ShouldBe("Other");
        await _variantRepository.DidNotReceiveWithAnyArgs().HasShiftsOutsidePeriodAsync(default, default, default, default);
    }

    [Test]
    public async Task Update_KeyOfAHiddenGroup_IsAnsweredLikeAMissingKey()
    {
        var variant = Variant(_hiddenGroupId);
        var command = new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest
        {
            Name = "Renamed",
            FromDate = From,
            UntilDate = Until
        });

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => UpdateHandler().Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(string.Format(ScenarioGroupVisibility.ScenarioNotFoundFormat, variant.Id));
        variant.Name.ShouldBe("Winter keys");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_UnknownKey_IsAnsweredWithNotFound()
    {
        var missingId = Guid.NewGuid();
        _variantRepository.GetVariantAsync(missingId, Arg.Any<CancellationToken>()).Returns((AnalyseScenario?)null);
        var command = new UpdateScenarioVariantCommand(missingId, new UpdateScenarioVariantRequest
        {
            Name = "Renamed",
            FromDate = From,
            UntilDate = Until
        });

        await Should.ThrowAsync<KeyNotFoundException>(
            () => UpdateHandler().Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Update_UntilBeforeFrom_IsRefusedAndChangesNothing()
    {
        var variant = Variant(_visibleGroupId);
        var command = new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest
        {
            Name = "Renamed",
            FromDate = Until,
            UntilDate = From
        });

        await Should.ThrowAsync<InvalidRequestException>(
            () => UpdateHandler().Handle(command, CancellationToken.None));

        variant.Name.ShouldBe("Winter keys");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_WhileAnActiveScenarioCarriesTheKey_ThrowsConflictAndDeletesNothing()
    {
        var variant = Variant(_visibleGroupId);
        _variantRepository.HasActiveScenarioAsync(variant.Id, Arg.Any<CancellationToken>()).Returns(true);

        var exception = await Should.ThrowAsync<ConflictException>(
            () => DeleteHandler().Handle(new DeleteScenarioVariantCommand(variant.Id), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.InUseMessage);
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Delete(default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_ConsumedKey_ThrowsConflictAndDeletesNothing()
    {
        var variant = Variant(_visibleGroupId, AnalyseScenarioStatus.Accepted);

        var exception = await Should.ThrowAsync<ConflictException>(
            () => DeleteHandler().Handle(new DeleteScenarioVariantCommand(variant.Id), CancellationToken.None));

        exception.Message.ShouldBe(ScenarioVariantAccess.ConsumedMessage);
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Delete(default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_KeyOfAHiddenGroup_IsAnsweredLikeAMissingKeyAndDeletesNothing()
    {
        var variant = Variant(_hiddenGroupId);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => DeleteHandler().Handle(new DeleteScenarioVariantCommand(variant.Id), CancellationToken.None));

        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().Delete(default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_UnusedKey_SoftDeletesItsDataThenTheRowThenTheFinishedLinksAndSaves()
    {
        var variant = Variant(_visibleGroupId);
        var order = new List<string>();
        _scenarioService.When(s => s.SoftDeleteScenarioDataAsync(variant.Token, CancellationToken.None))
            .Do(_ => order.Add(nameof(IAnalyseScenarioService.SoftDeleteScenarioDataAsync)));
        _scenarioRepository.When(r => r.Delete(variant.Id))
            .Do(_ => order.Add(nameof(IBaseRepository<AnalyseScenario>.Delete)));
        _variantRepository.When(r => r.SoftDeleteFinishedScenarioLinksAsync(variant.Id, CancellationToken.None))
            .Do(_ => order.Add(nameof(IScenarioVariantRepository.SoftDeleteFinishedScenarioLinksAsync)));
        _unitOfWork.When(u => u.CompleteAsync())
            .Do(_ => order.Add(nameof(IUnitOfWork.CompleteAsync)));

        var result = await DeleteHandler().Handle(new DeleteScenarioVariantCommand(variant.Id), CancellationToken.None);

        result.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            nameof(IAnalyseScenarioService.SoftDeleteScenarioDataAsync),
            nameof(IBaseRepository<AnalyseScenario>.Delete),
            nameof(IScenarioVariantRepository.SoftDeleteFinishedScenarioLinksAsync),
            nameof(IUnitOfWork.CompleteAsync)
        });
    }

    [Test]
    public async Task List_VisibleGroup_ReturnsTheVariantsWithTheirServiceCounts()
    {
        var first = Variant(_visibleGroupId, name: "A keys");
        var second = Variant(_visibleGroupId, AnalyseScenarioStatus.Accepted, "B keys");
        _variantRepository.ListByGroupsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { first, second });
        _variantRepository.CountShiftsByVariantTokenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(tokens => tokens.SequenceEqual(new[] { first.Token, second.Token })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [first.Token] = 2, [second.Token] = 0 });

        var result = await ListHandler().Handle(new ListScenarioVariantsQuery(_visibleGroupId), CancellationToken.None);

        result.Select(r => r.Id).ShouldBe(new[] { first.Id, second.Id });
        result.Select(r => r.ShiftCount).ShouldBe(new[] { 2, 0 });
        result.Select(r => r.Status).ShouldBe(new[] { (int)AnalyseScenarioStatus.Active, (int)AnalyseScenarioStatus.Accepted });
        result[0].Token.ShouldBe(first.Token);
        result[0].GroupId.ShouldBe(_visibleGroupId);
    }

    [Test]
    public async Task List_ReadsTheGroupWithItsVisibleSubgroups_AndNoHiddenSubgroup()
    {
        _scenarioService.GetGroupHierarchyIdsAsync(_visibleGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _visibleGroupId, _visibleSubgroupId, _hiddenGroupId });
        var subgroupKey = Variant(_visibleSubgroupId);
        _variantRepository.ListByGroupsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { _visibleGroupId, _visibleSubgroupId })),
                Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { subgroupKey });
        _variantRepository.CountShiftsByVariantTokenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        var result = await ListHandler().Handle(new ListScenarioVariantsQuery(_visibleGroupId), CancellationToken.None);

        result.Single().Id.ShouldBe(subgroupKey.Id);
    }

    [Test]
    public async Task List_HiddenGroup_ReturnsAnEmptyListWithoutReadingTheRepository()
    {
        var variant = Variant(_hiddenGroupId);
        _variantRepository.ListByGroupsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { variant });

        var result = await ListHandler().Handle(new ListScenarioVariantsQuery(_hiddenGroupId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _variantRepository.DidNotReceiveWithAnyArgs().ListByGroupsAsync(default!, default);
    }

    [TestCase(2030, 3, 14, true)]
    [TestCase(2030, 3, 15, false)]
    [TestCase(2030, 3, 16, false)]
    public async Task List_IsExpired_FollowsTheCompanyClock(int year, int month, int day, bool expected)
    {
        var variant = Variant(_visibleGroupId, until: new DateOnly(year, month, day));
        _variantRepository.ListByGroupsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario> { variant });
        _variantRepository.CountShiftsByVariantTokenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(tokens => tokens.SequenceEqual(new[] { variant.Token })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [variant.Token] = 0 });

        var result = await ListHandler().Handle(new ListScenarioVariantsQuery(_visibleGroupId), CancellationToken.None);

        result.Single().IsExpired.ShouldBe(expected);
    }

    [Test]
    public async Task Create_IsExpired_FollowsTheCompanyClock()
    {
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Until.AddDays(1));
        var command = new CreateScenarioVariantCommand(CreateRequest(_visibleGroupId));

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        result.IsExpired.ShouldBeTrue();
    }

    [Test]
    public async Task Update_IsExpired_FollowsTheCompanyClock()
    {
        var variant = Variant(_visibleGroupId);
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Until.AddDays(1));
        _variantRepository.CountShiftsByVariantTokenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(tokens => tokens.SequenceEqual(new[] { variant.Token })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [variant.Token] = 0 });
        var command = new UpdateScenarioVariantCommand(variant.Id, new UpdateScenarioVariantRequest
        {
            Name = variant.Name,
            FromDate = From,
            UntilDate = Until
        });

        var result = await UpdateHandler().Handle(command, CancellationToken.None);

        result.IsExpired.ShouldBeTrue();
    }

    [Test]
    public async Task GetShifts_VisibleKey_ReturnsTheServicesInRepositoryOrder()
    {
        var variant = Variant(_visibleGroupId);
        var first = SealedShift("A service");
        var second = SealedShift("B service");
        _variantRepository.GetSealedShiftsAsync(variant.Token, Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { first, second });

        var result = await ShiftsHandler().Handle(new GetScenarioVariantShiftsQuery(variant.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Select(r => r.Id).ShouldBe(new[] { first.Id, second.Id });
        result.Select(r => r.Name).ShouldBe(new[] { "A service", "B service" });
        result.ShouldAllBe(r => r.Status == ShiftStatus.SealedOrder);
    }

    [Test]
    public async Task GetShifts_KeyWithoutServices_ReturnsAnEmptyList()
    {
        var variant = Variant(_visibleGroupId);
        _variantRepository.GetSealedShiftsAsync(variant.Token, Arg.Any<CancellationToken>())
            .Returns(new List<Shift>());

        var result = await ShiftsHandler().Handle(new GetScenarioVariantShiftsQuery(variant.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetShifts_KeyOfAHiddenGroup_IsAnsweredWithNullWithoutLoadingShifts()
    {
        var variant = Variant(_hiddenGroupId);
        _variantRepository.GetSealedShiftsAsync(variant.Token, Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { SealedShift("A service") });

        var result = await ShiftsHandler().Handle(new GetScenarioVariantShiftsQuery(variant.Id), CancellationToken.None);

        result.ShouldBeNull();
        await _variantRepository.DidNotReceive().GetSealedShiftsAsync(variant.Token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetShifts_UnknownKey_IsAnsweredWithNull()
    {
        var missingId = Guid.NewGuid();
        _variantRepository.GetVariantAsync(missingId, Arg.Any<CancellationToken>()).Returns((AnalyseScenario?)null);

        var result = await ShiftsHandler().Handle(new GetScenarioVariantShiftsQuery(missingId), CancellationToken.None);

        result.ShouldBeNull();
    }
}
