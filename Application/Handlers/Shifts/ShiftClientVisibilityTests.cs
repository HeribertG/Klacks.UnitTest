// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Customer reference of the shift POST and PUT handlers: a customer that is hidden from the caller, missing or
/// soft-deleted (the repository's query filter answers it like missing) is refused with the same 404 text before
/// anything is written; PUT checks the customer only when it changes, so an unchanged hidden customer stays saveable;
/// a locked shift answers 409 whatever customer is sent. Uses the real ClientReferenceGuard over substituted
/// visibility guard and client repository.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class ShiftClientVisibilityTests
{
    private const string IdPlaceholder = "<id>";

    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IClientRepository _clientRepository = null!;
    private IOrderSealingService _orderSealingService = null!;
    private HashSet<Guid> _hiddenClientIds = null!;
    private HashSet<Guid> _existingClientIds = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _orderSealingService = Substitute.For<IOrderSealingService>();
        _orderSealingService.CollectMissingRequirements(Arg.Any<Shift>()).Returns(Array.Empty<string>());

        _hiddenClientIds = [];
        _existingClientIds = [];
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => !_hiddenClientIds.Contains(call.ArgAt<Guid>(0)));
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.Exists(Arg.Any<Guid>()).Returns(call => _existingClientIds.Contains(call.ArgAt<Guid>(0)));
    }

    [Test]
    public async Task Post_HiddenAndMissingCustomer_AreRefusedWithTheSame404_AndNothingIsWritten()
    {
        var hiddenId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _existingClientIds.Add(hiddenId);
        _hiddenClientIds.Add(hiddenId);

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => PostAsync(hiddenId));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => PostAsync(missingId));

        hidden.Message.ShouldBe(string.Format(ClientVisibilityMessages.ClientNotFound, hiddenId));
        hidden.Message.Replace(hiddenId.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingId.ToString(), IdPlaceholder));
        await _shiftRepository.DidNotReceiveWithAnyArgs().AddWithSealedOrderHandling(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_Unrestricted_UnknownOrSoftDeletedCustomer_Is404_InsteadOfAForeignKeyError()
    {
        var unknownId = Guid.NewGuid();

        var error = await Should.ThrowAsync<KeyNotFoundException>(() => PostAsync(unknownId));

        error.Message.ShouldBe(string.Format(ClientVisibilityMessages.ClientNotFound, unknownId));
        await _clientRepository.Received(1).Exists(unknownId);
        await _shiftRepository.DidNotReceiveWithAnyArgs().AddWithSealedOrderHandling(default!);
    }

    [Test]
    public async Task Post_VisibleExistingCustomer_IsCreated()
    {
        var clientId = Guid.NewGuid();
        _existingClientIds.Add(clientId);

        var result = await PostAsync(clientId);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.ClientId == clientId));
    }

    [Test]
    public async Task Post_WithoutCustomer_DoesNotConsultTheCustomerChecks()
    {
        var handler = PostHandler();
        var resource = new ShiftResource { Name = "Ward night shift", Status = ShiftStatus.SealedOrder };

        var result = await handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientVisibilityGuard.DidNotReceiveWithAnyArgs().IsVisibleAsync(default);
        await _clientRepository.DidNotReceiveWithAnyArgs().Exists(default);
    }

    [Test]
    public async Task Put_UnchangedHiddenCustomer_IsSaved_WithoutCustomerCheck()
    {
        var hiddenId = Guid.NewGuid();
        _hiddenClientIds.Add(hiddenId);
        var stored = StoredDraft(hiddenId);

        var result = await PutAsync(stored, hiddenId);

        result.ShouldNotBeNull();
        await _clientVisibilityGuard.DidNotReceiveWithAnyArgs().IsVisibleAsync(default);
        await _clientRepository.DidNotReceiveWithAnyArgs().Exists(default);
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_ChangedToHiddenOrMissingCustomer_IsRefusedWithTheSame404_AndNothingIsWritten()
    {
        var hiddenId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _existingClientIds.Add(hiddenId);
        _hiddenClientIds.Add(hiddenId);
        var stored = StoredDraft(Guid.NewGuid());

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(() => PutAsync(stored, hiddenId));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(() => PutAsync(stored, missingId));

        hidden.Message.Replace(hiddenId.ToString(), IdPlaceholder)
            .ShouldBe(missing.Message.Replace(missingId.ToString(), IdPlaceholder));
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_Unrestricted_ChangedToUnknownCustomer_Is404_InsteadOfAForeignKeyError()
    {
        var unknownId = Guid.NewGuid();

        var error = await Should.ThrowAsync<KeyNotFoundException>(() => PutAsync(StoredDraft(null), unknownId));

        error.Message.ShouldBe(string.Format(ClientVisibilityMessages.ClientNotFound, unknownId));
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
    }

    [Test]
    public async Task Put_ChangedToVisibleExistingCustomer_IsSaved()
    {
        var newClientId = Guid.NewGuid();
        _existingClientIds.Add(newClientId);

        var result = await PutAsync(StoredDraft(Guid.NewGuid()), newClientId);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.ClientId == newClientId));
    }

    [Test]
    public async Task Put_LockedShift_ChangedCustomer_Answers409_WhetherTheCustomerIsHiddenOrNot()
    {
        var hiddenId = Guid.NewGuid();
        var visibleId = Guid.NewGuid();
        _existingClientIds.Add(hiddenId);
        _existingClientIds.Add(visibleId);
        _hiddenClientIds.Add(hiddenId);
        var stored = StoredDraft(Guid.NewGuid());
        stored.Status = ShiftStatus.SealedOrder;

        await Should.ThrowAsync<ShiftLockedException>(() => PutAsync(stored, hiddenId));
        await Should.ThrowAsync<ShiftLockedException>(() => PutAsync(stored, visibleId));

        await _clientVisibilityGuard.DidNotReceiveWithAnyArgs().IsVisibleAsync(default);
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
    }

    private Task<ShiftResource?> PostAsync(Guid clientId)
    {
        var resource = new ShiftResource
        {
            Name = "Early shift",
            Status = ShiftStatus.OriginalOrder,
            MacroId = Guid.NewGuid(),
            ClientId = clientId
        };
        return PostHandler().Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);
    }

    private Task<ShiftResource?> PutAsync(Shift stored, Guid clientId)
    {
        _shiftRepository.GetNoTracking(stored.Id).Returns(stored);
        var mapper = new ScheduleMapper();
        var resource = mapper.ToShiftResource(stored);
        resource.ClientId = clientId;
        resource.Client = null;
        var handler = new PutCommandHandler(
            _shiftRepository,
            Substitute.For<IShiftRequiredQualificationRepository>(),
            mapper,
            _unitOfWork,
            ShiftVisibilityGuards.AllowAll(),
            new ClientReferenceGuard(_clientVisibilityGuard, _clientRepository),
            Substitute.For<ILogger<PutCommandHandler>>());
        return handler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);
    }

    private PostCommandHandler PostHandler() => new(
        _shiftRepository,
        new ScheduleMapper(),
        _unitOfWork,
        Substitute.For<IDefaultShiftMacroResolver>(),
        _orderSealingService,
        ShiftVisibilityGuards.AllowAll(),
        new ClientReferenceGuard(_clientVisibilityGuard, _clientRepository),
        Substitute.For<ILogger<PostCommandHandler>>());

    private static Shift StoredDraft(Guid? clientId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Stored order",
        Status = ShiftStatus.OriginalOrder,
        FromDate = new DateOnly(2026, 1, 1),
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0),
        ClientId = clientId
    };
}
