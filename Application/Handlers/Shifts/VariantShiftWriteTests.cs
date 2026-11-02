// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the shift handlers in a variant (key), with the real VariantShiftWriteGuard over stubbed repositories. POST with a
/// variant token stages a sealed order that carries the token on the shift and every group item; every other token, a
/// consumed key, a hidden key, a missing customer, a shift without a group and a group outside the key group are refused
/// before any write. PUT and DELETE decide from the stored token, not from the request: a consumed or hidden key is
/// refused, DELETE removes the whole family wherever it was addressed, and a scenario clone keeps reaching the repository
/// refusal. GET serves a key shift of a visible key and keeps refusing scenario clones. That the sealed order really gets
/// its plannable copy is proven against PostgreSQL in VariantShiftWriteSeamTests, because the repository is stubbed here.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Services.ScenarioVariants;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Filters;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class VariantShiftWriteTests
{
    private static readonly DateOnly KeyFrom = new(2099, 8, 1);
    private static readonly DateOnly KeyUntil = new(2099, 8, 31);

    private IShiftRepository _shiftRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScenarioVariantRepository _variantRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private VariantShiftWriteGuard _writeGuard = null!;
    private Guid _keyGroupId;
    private Guid _subGroupId;
    private Guid _foreignGroupId;
    private bool _keyGroupVisible;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _variantRepository = Substitute.For<IScenarioVariantRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _keyGroupId = Guid.NewGuid();
        _subGroupId = Guid.NewGuid();
        _foreignGroupId = Guid.NewGuid();
        _keyGroupVisible = true;

        _groupGuard.IsGroupVisibleAsync(_keyGroupId, Arg.Any<CancellationToken>())
            .Returns(_ => _keyGroupVisible);
        _scenarioService.GetGroupHierarchyIdsAsync(_keyGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _keyGroupId, _subGroupId });

        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);

        _writeGuard = new VariantShiftWriteGuard(_scenarioRepository, _variantRepository, _scenarioService, _groupGuard);
    }

    [Test]
    public async Task Post_WithVariantToken_StagesASealedOrderWithTheTokenOnTheShiftAndEveryGroupItem()
    {
        var variant = StubVariant();
        Shift? staged = null;
        _shiftRepository.AddWithSealedOrderHandling(Arg.Do<Shift>(shift => staged = shift)).Returns(call => call.Arg<Shift>());

        await PostHandler().Handle(
            new PostCommand<ShiftResource>(KeyResource(variant.Token, _keyGroupId, _subGroupId)), CancellationToken.None);

        staged.ShouldNotBeNull();
        staged.AnalyseToken.ShouldBe(variant.Token);
        staged.Status.ShouldBe(ShiftStatus.SealedOrder);
        staged.GroupItems.Count.ShouldBe(2);
        staged.GroupItems.ShouldAllBe(item => item.AnalyseToken == variant.Token);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Post_WithoutToken_StaysAPlainDraftAndNeverAsksForAVariant()
    {
        Shift? staged = null;
        _shiftRepository.AddWithSealedOrderHandling(Arg.Do<Shift>(shift => staged = shift)).Returns(call => call.Arg<Shift>());
        var resource = new ShiftResource { Name = "Early", ClientId = Guid.NewGuid(), Status = ShiftStatus.OriginalOrder };

        await PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        staged.ShouldNotBeNull();
        staged.AnalyseToken.ShouldBeNull();
        staged.Status.ShouldBe(ShiftStatus.OriginalOrder);
        await _scenarioRepository.DidNotReceive().GetByTokenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Post_WithTheTokenOfANormalScenario_IsRefused()
    {
        var scenario = StubVariant(kind: AnalyseScenarioKind.Scenario);

        await ShouldRefuseWithoutWriteAsync<BadRequestException>(KeyResource(scenario.Token, _keyGroupId));
    }

    [Test]
    public async Task Post_WithAnUnknownToken_IsRefused()
    {
        await ShouldRefuseWithoutWriteAsync<BadRequestException>(KeyResource(Guid.NewGuid(), _keyGroupId));
    }

    [Test]
    public async Task Post_IntoAConsumedKey_IsAConflict()
    {
        var variant = StubVariant(AnalyseScenarioStatus.Accepted);

        await ShouldRefuseWithoutWriteAsync<ConflictException>(KeyResource(variant.Token, _keyGroupId));
    }

    [Test]
    public async Task Post_IntoAKeyOfAHiddenGroup_IsAnsweredLikeMissing()
    {
        var variant = StubVariant();
        _keyGroupVisible = false;

        await ShouldRefuseWithoutWriteAsync<KeyNotFoundException>(KeyResource(variant.Token, _keyGroupId));
    }

    [Test]
    public async Task Post_WithoutACustomer_IsRefusedAlthoughASealedOrderMayOtherwiseHaveNone()
    {
        var variant = StubVariant();
        var resource = KeyResource(variant.Token, _keyGroupId);
        resource.ClientId = null;

        await ShouldRefuseWithoutWriteAsync<BadRequestException>(resource);
    }

    [Test]
    public async Task Post_WithoutAGroup_IsRefused_BecauseAGroupLessShiftIsVisibleToEveryone()
    {
        var variant = StubVariant();

        await ShouldRefuseWithoutWriteAsync<BadRequestException>(KeyResource(variant.Token));
    }

    [Test]
    public async Task Post_WithAGroupOutsideTheKeyGroup_IsRefused_EvenNextToAValidGroup()
    {
        var variant = StubVariant();

        await ShouldRefuseWithoutWriteAsync<BadRequestException>(KeyResource(variant.Token, _keyGroupId, _foreignGroupId));
    }

    [Test]
    public async Task Post_InsideTheKeyPeriodUpToBothBoundaries_IsAccepted()
    {
        var variant = StubVariant();

        await PostHandler().Handle(
            new PostCommand<ShiftResource>(KeyResource(variant.Token, _keyGroupId)), CancellationToken.None);

        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [TestCase(-1, 0)]
    [TestCase(0, 1)]
    public async Task Post_OutsideTheKeyPeriod_IsRefused(int fromOffset, int untilOffset)
    {
        var variant = StubVariant();
        var resource = KeyResource(variant.Token, _keyGroupId);
        resource.FromDate = KeyFrom.AddDays(fromOffset);
        resource.UntilDate = KeyUntil.AddDays(untilOffset);

        var refusal = await Should.ThrowAsync<BadRequestException>(
            () => PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Post_WithoutAnEnd_IsRefused_BecauseAKeyShiftAlwaysEnds()
    {
        var variant = StubVariant();
        var resource = KeyResource(variant.Token, _keyGroupId);
        resource.UntilDate = null;

        var refusal = await Should.ThrowAsync<BadRequestException>(
            () => PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
    }

    [Test]
    public async Task Put_CannotMoveAKeyShiftOutOfTheKeyPeriod()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);
        var resource = ResourceOf(stored, _keyGroupId);
        resource.UntilDate = KeyUntil.AddDays(1);

        var refusal = await Should.ThrowAsync<BadRequestException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Put_CannotClearTheEndOfAKeyShift()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);
        var resource = ResourceOf(stored, _keyGroupId);
        resource.UntilDate = null;

        var refusal = await Should.ThrowAsync<BadRequestException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        refusal.Message.ShouldBe(VariantShiftWriteGuard.PeriodOutsideVariantMessage);
    }

    [Test]
    public async Task EnsureInsideVariantPeriod_RefusesARangeOutsideTheKeyAndAnEmptyEnd()
    {
        var variant = StubVariant();

        await _writeGuard.EnsureInsideVariantPeriodAsync(variant.Token, [(KeyFrom, KeyUntil)]);
        await Should.ThrowAsync<BadRequestException>(
            () => _writeGuard.EnsureInsideVariantPeriodAsync(variant.Token, [(KeyFrom, KeyUntil), (KeyFrom, null)]));
        await Should.ThrowAsync<BadRequestException>(
            () => _writeGuard.EnsureInsideVariantPeriodAsync(variant.Token, [(KeyUntil.AddDays(1), KeyUntil.AddDays(2))]));
    }

    [Test]
    public async Task EnsureInsideVariantPeriod_LeavesATokenOfAScenarioAlone()
    {
        var scenario = StubVariant(kind: AnalyseScenarioKind.Scenario);

        await _writeGuard.EnsureInsideVariantPeriodAsync(scenario.Token, [(KeyFrom, null)]);
    }

    [Test]
    public void Mapper_NeverTakesTheTokenFromTheResource()
    {
        var entity = new ScheduleMapper().ToShiftEntity(KeyResource(Guid.NewGuid(), _keyGroupId));

        entity.AnalyseToken.ShouldBeNull();
        entity.GroupItems.ShouldAllBe(item => item.AnalyseToken == null);
    }

    [Test]
    public void ShiftCloner_CopiesTheTokenOntoTheCopyAndItsGroupItemsButNotOntoItsExpenses()
    {
        var token = Guid.NewGuid();
        var source = new Shift
        {
            AnalyseToken = token,
            GroupItems = [new GroupItem { GroupId = _keyGroupId, AnalyseToken = token }],
            ShiftExpenses = [new ShiftExpenses { Description = "Train", AnalyseToken = null }]
        };

        var copy = ShiftCloner.Clone(source);

        copy.AnalyseToken.ShouldBe(token);
        copy.GroupItems.Single().AnalyseToken.ShouldBe(token);
        copy.ShiftExpenses.Single().AnalyseToken.ShouldBeNull();
    }

    [Test]
    public async Task Put_OfAKeyShift_WithoutTokenInTheBody_StillPassesTheChecksAndReachesTheRepository()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);

        await PutHandler().Handle(new PutCommand<ShiftResource>(ResourceOf(stored, _subGroupId)), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(shift => shift.Id == stored.Id));
    }

    [Test]
    public async Task Put_OfAKeyShift_IsCheckedAgainstTheStoredToken_NotTheBody()
    {
        var variant = StubVariant(AnalyseScenarioStatus.Accepted);
        var stored = StubStored(variant.Token);
        var resource = ResourceOf(stored, _keyGroupId);
        resource.AnalyseToken = Guid.NewGuid();

        await ShouldRefusePutAsync<ConflictException>(resource);
    }

    [Test]
    public async Task Put_OfAKeyShift_OfAHiddenKey_IsAnsweredLikeMissing()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);
        _keyGroupVisible = false;

        await ShouldRefusePutAsync<KeyNotFoundException>(ResourceOf(stored, _keyGroupId));
    }

    [Test]
    public async Task Put_CannotMoveAKeyShiftOutOfTheKeyGroup()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);

        await ShouldRefusePutAsync<BadRequestException>(ResourceOf(stored, _foreignGroupId));
    }

    [Test]
    public async Task Put_CannotRemoveTheLastGroupOfAKeyShift()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);

        await ShouldRefusePutAsync<BadRequestException>(ResourceOf(stored));
    }

    [Test]
    public async Task Put_CannotRemoveTheCustomerOfAKeyShift()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);
        var resource = ResourceOf(stored, _keyGroupId);
        resource.ClientId = null;

        await ShouldRefusePutAsync<BadRequestException>(resource);
    }

    [Test]
    public async Task Put_CannotChangeTheStatusOfAKeyShift_BecauseTheLockPolicySkipsTokenShifts()
    {
        var variant = StubVariant();
        var stored = StubStored(variant.Token);
        var resource = ResourceOf(stored, _keyGroupId);
        resource.Status = ShiftStatus.OriginalOrder;

        await ShouldRefusePutAsync<BadRequestException>(resource);
    }

    [Test]
    public async Task Put_OfAScenarioClone_SkipsTheKeyChecksAndIsLeftToTheRepositoryRefusal()
    {
        var scenario = StubVariant(kind: AnalyseScenarioKind.Scenario);
        var stored = StubStored(scenario.Token);

        await PutHandler().Handle(new PutCommand<ShiftResource>(ResourceOf(stored)), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(shift => shift.Id == stored.Id));
    }

    [Test]
    public async Task Delete_OfAKeyShift_RemovesTheWholeFamily_EvenWhenAddressedByItsPlannableCopy()
    {
        var variant = StubVariant();
        var sealedOrderId = Guid.NewGuid();
        var copy = StubExisting(variant.Token, originalId: sealedOrderId);
        List<Guid> family = [sealedOrderId, copy.Id];
        _variantRepository.GetShiftFamilyIdsAsync(variant.Token, sealedOrderId, Arg.Any<CancellationToken>()).Returns(family);

        var result = await DeleteHandler().Handle(new DeleteCommand<ShiftResource>(copy.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).DeleteOrderFamilyAsync(family, Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_OfAKeyShift_InAConsumedKey_IsAConflict()
    {
        var variant = StubVariant(AnalyseScenarioStatus.Accepted);
        var shift = StubExisting(variant.Token);

        await ShouldRefuseDeleteAsync<ConflictException>(shift.Id);
    }

    [Test]
    public async Task Delete_OfAKeyShift_OfAHiddenKey_IsAnsweredLikeMissing()
    {
        var variant = StubVariant();
        var shift = StubExisting(variant.Token);
        _keyGroupVisible = false;

        await ShouldRefuseDeleteAsync<KeyNotFoundException>(shift.Id);
    }

    [Test]
    public async Task Delete_OfAKeyShift_ThatAScenarioCloned_IsAConflict()
    {
        var variant = StubVariant();
        var shift = StubExisting(variant.Token);
        List<Guid> family = [shift.Id];
        _variantRepository.GetShiftFamilyIdsAsync(variant.Token, shift.Id, Arg.Any<CancellationToken>()).Returns(family);
        _variantRepository.HasScenarioClonesAsync(family, Arg.Any<CancellationToken>()).Returns(true);

        await ShouldRefuseDeleteAsync<ConflictException>(shift.Id);
    }

    [Test]
    public async Task Delete_OfAScenarioClone_IsLeftToTheRepositoryRefusal()
    {
        var scenario = StubVariant(kind: AnalyseScenarioKind.Scenario);
        var clone = StubExisting(scenario.Token);

        await DeleteHandler().Handle(new DeleteCommand<ShiftResource>(clone.Id), CancellationToken.None);

        await _shiftRepository.Received(1).Delete(clone.Id);
        await _shiftRepository.DidNotReceive().DeleteOrderFamilyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Get_ServesAKeyShiftOfAVisibleKey()
    {
        var token = Guid.NewGuid();
        var shift = StubExisting(token);
        var tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        tokenGuard.IsVariantTokenVisibleAsync(token, Arg.Any<CancellationToken>()).Returns(true);

        var result = await GetHandler(tokenGuard).Handle(new GetQuery<ShiftResource>(shift.Id), CancellationToken.None);

        result.Id.ShouldBe(shift.Id);
        result.AnalyseToken.ShouldBe(token);
    }

    [Test]
    public async Task Get_KeepsRefusingAShiftWhoseTokenIsNoVisibleKey()
    {
        var token = Guid.NewGuid();
        var shift = StubExisting(token);
        var tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        tokenGuard.IsVariantTokenVisibleAsync(token, Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<InvalidRequestException>(
            () => GetHandler(tokenGuard).Handle(new GetQuery<ShiftResource>(shift.Id), CancellationToken.None));
    }

    [Test]
    public async Task Get_AnswersAShiftOfAHiddenKey_ExactlyLikeAMissingId()
    {
        var token = Guid.NewGuid();
        var shift = StubExisting(token);
        var tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        tokenGuard.IsHiddenVariantTokenAsync(token, Arg.Any<CancellationToken>()).Returns(true);

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => GetHandler(tokenGuard).Handle(new GetQuery<ShiftResource>(shift.Id), CancellationToken.None));
        var missingId = Guid.NewGuid();
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => GetHandler(tokenGuard).Handle(new GetQuery<ShiftResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe(missing.Message.Replace(missingId.ToString(), shift.Id.ToString()));
    }

    [Test]
    public async Task Get_KeepsRefusingAClone_AlsoWhenItsTokenLooksLikeAKey()
    {
        var token = Guid.NewGuid();
        var shift = StubExisting(token);
        shift.ScenarioSourceShiftId = Guid.NewGuid();
        var tokenGuard = Substitute.For<IScenarioTokenAccessGuard>();
        tokenGuard.IsVariantTokenVisibleAsync(token, Arg.Any<CancellationToken>()).Returns(true);

        await Should.ThrowAsync<InvalidRequestException>(
            () => GetHandler(tokenGuard).Handle(new GetQuery<ShiftResource>(shift.Id), CancellationToken.None));
    }

    [Test]
    public async Task EnsureOpenIfVariant_OfAnOpenVisibleKey_IsTrue()
    {
        var variant = StubVariant();

        (await _writeGuard.EnsureOpenIfVariantAsync(variant.Token, Guid.NewGuid())).ShouldBeTrue();
    }

    [Test]
    public async Task EnsureOpenIfVariant_OfAScenarioOrAnUnknownToken_IsFalse()
    {
        var scenario = StubVariant(kind: AnalyseScenarioKind.Scenario);

        (await _writeGuard.EnsureOpenIfVariantAsync(scenario.Token, Guid.NewGuid())).ShouldBeFalse();
        (await _writeGuard.EnsureOpenIfVariantAsync(Guid.NewGuid(), Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task EnsureOpenIfVariant_OfAConsumedKey_IsAConflict()
    {
        var variant = StubVariant(AnalyseScenarioStatus.Accepted);

        await Should.ThrowAsync<ConflictException>(() => _writeGuard.EnsureOpenIfVariantAsync(variant.Token, Guid.NewGuid()));
    }

    [Test]
    public async Task EnsureOpenIfVariant_OfAHiddenKey_IsAnsweredLikeAMissingOne()
    {
        var variant = StubVariant();
        _keyGroupVisible = false;

        var shiftId = Guid.NewGuid();

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() => _writeGuard.EnsureOpenIfVariantAsync(variant.Token, shiftId));

        ex.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, shiftId));
        ex.Message.ShouldNotContain(variant.Id.ToString());
    }

    [TestCase(nameof(ShiftsController.Post))]
    [TestCase(nameof(ShiftsController.Put))]
    public void ShiftWrites_AllowTheVariantToken_AndInheritTheirRolesUnchanged(string actionName)
    {
        var action = ControllerAction(actionName);

        action.GetCustomAttribute<AllowVariantTokenAttribute>().ShouldNotBeNull();
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ShouldBeEmpty();
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Roles
            .ShouldBe($"{Klacks.Api.Domain.Constants.Roles.Admin},{Klacks.Api.Domain.Constants.Roles.Authorised}");
    }

    [Test]
    public void ShiftDelete_StaysAdminOnly_AndNeedsNoVariantTokenDoor()
    {
        var action = ControllerAction(nameof(ShiftsController.Delete));

        action.GetCustomAttribute<AllowVariantTokenAttribute>().ShouldBeNull();
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Single().Roles
            .ShouldBe(Klacks.Api.Domain.Constants.Roles.Admin);
    }

    private static MethodInfo ControllerAction(string name)
    {
        return typeof(ShiftsController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method => method.Name == name);
    }

    private AnalyseScenario StubVariant(
        AnalyseScenarioStatus status = AnalyseScenarioStatus.Active,
        AnalyseScenarioKind kind = AnalyseScenarioKind.Variant)
    {
        var row = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            Name = "Large order",
            GroupId = _keyGroupId,
            Kind = kind,
            Status = status,
            FromDate = KeyFrom,
            UntilDate = KeyUntil
        };
        _scenarioRepository.GetByTokenAsync(row.Token, Arg.Any<CancellationToken>()).Returns(row);
        return row;
    }

    private Shift StubStored(Guid token)
    {
        var stored = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Key shift",
            ClientId = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            FromDate = KeyFrom,
            UntilDate = KeyUntil,
            AnalyseToken = token
        };
        _shiftRepository.GetNoTracking(stored.Id).Returns(stored);
        return stored;
    }

    private Shift StubExisting(Guid token, Guid? originalId = null)
    {
        var existing = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Key shift",
            ClientId = Guid.NewGuid(),
            Status = originalId == null ? ShiftStatus.SealedOrder : ShiftStatus.OriginalShift,
            OriginalId = originalId,
            AnalyseToken = token
        };
        _shiftRepository.Get(existing.Id).Returns(existing);
        return existing;
    }

    private static ShiftResource KeyResource(Guid token, params Guid[] groupIds)
    {
        return new ShiftResource
        {
            Name = "Key shift",
            ClientId = Guid.NewGuid(),
            Status = ShiftStatus.OriginalOrder,
            FromDate = KeyFrom,
            UntilDate = KeyUntil,
            AnalyseToken = token,
            Groups = groupIds.Select(id => new SimpleGroupResource { Id = id }).ToList()
        };
    }

    private static ShiftResource ResourceOf(Shift stored, params Guid[] groupIds)
    {
        var resource = new ScheduleMapper().ToShiftResource(stored);
        resource.AnalyseToken = null;
        resource.Client = null;
        resource.Groups = groupIds.Select(id => new SimpleGroupResource { Id = id }).ToList();
        return resource;
    }

    private async Task ShouldRefuseWithoutWriteAsync<TException>(ShiftResource resource)
        where TException : Exception
    {
        await Should.ThrowAsync<TException>(
            () => PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None));

        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private async Task ShouldRefusePutAsync<TException>(ShiftResource resource)
        where TException : Exception
    {
        await Should.ThrowAsync<TException>(
            () => PutHandler().Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private async Task ShouldRefuseDeleteAsync<TException>(Guid shiftId)
        where TException : Exception
    {
        await Should.ThrowAsync<TException>(
            () => DeleteHandler().Handle(new DeleteCommand<ShiftResource>(shiftId), CancellationToken.None));

        await _shiftRepository.DidNotReceive().DeleteOrderFamilyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private PostCommandHandler PostHandler() => new(
        _shiftRepository,
        new ScheduleMapper(),
        _unitOfWork,
        Substitute.For<IDefaultShiftMacroResolver>(),
        Substitute.For<IOrderSealingService>(),
        ShiftVisibilityGuards.AllowAll(),
        Substitute.For<IClientReferenceGuard>(),
        _writeGuard,
        Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler PutHandler() => new(
        _shiftRepository,
        Substitute.For<IShiftRequiredQualificationRepository>(),
        new ScheduleMapper(),
        _unitOfWork,
        ShiftVisibilityGuards.AllowAll(),
        Substitute.For<IClientReferenceGuard>(),
        _writeGuard,
        Substitute.For<Klacks.Api.Application.Interfaces.IShiftFamilyEndService>(),
        Substitute.For<ILogger<PutCommandHandler>>());

    private DeleteCommandHandler DeleteHandler() => new(
        _shiftRepository,
        new ScheduleMapper(),
        _unitOfWork,
        Substitute.For<ISealedOrderFamilyDeletion>(),
        Substitute.For<IHttpContextAccessor>(),
        ShiftVisibilityGuards.AllowAll(),
        _writeGuard,
        Substitute.For<ILogger<DeleteCommandHandler>>());

    private GetQueryHandler GetHandler(IScenarioTokenAccessGuard tokenGuard)
    {
        var groupFilter = Substitute.For<IShiftGroupFilterService>();
        groupFilter.ResolveShiftListGroupIdsAsync(Arg.Any<Guid?>()).Returns(Task.FromResult<List<Guid>?>(null));
        return new GetQueryHandler(
            _shiftRepository, groupFilter, new ScheduleMapper(), tokenGuard, Substitute.For<ILogger<GetQueryHandler>>());
    }
}
