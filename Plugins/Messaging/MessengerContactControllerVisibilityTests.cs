// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-04 the client messenger contacts had neither a role check nor group visibility: any signed-in
/// user could bind an own chat id to a client of a foreign group, and inbound automation then acted for that
/// client. A contact of a hidden client must now be answered exactly like a missing one, nothing may be written
/// for it, and only admins and supervisors may write at all.
/// </summary>

using System.Reflection;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.DTOs;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Presentation.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessengerContactControllerVisibilityTests
{
    private const string AttackerChatId = "884411223";
    private const string ExistingChatId = "112233445";

    private IMessengerContactRepository _repository = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private IClientVisibilityReader _clientVisibility = null!;
    private MessengerContactController _sut = null!;
    private Guid _hiddenClientId;
    private Guid _visibleClientId;
    private MessengerContact _hiddenContact = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IMessengerContactRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        _clientVisibility = Substitute.For<IClientVisibilityReader>();
        _hiddenClientId = Guid.NewGuid();
        _visibleClientId = Guid.NewGuid();
        _clientVisibility.IsClientVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Guid>() != _hiddenClientId);

        _hiddenContact = new MessengerContact
        {
            Id = Guid.NewGuid(),
            ClientId = _hiddenClientId,
            Type = MessengerType.Telegram,
            Value = ExistingChatId,
        };
        _repository.GetByIdAsync(_hiddenContact.Id, Arg.Any<CancellationToken>()).Returns(_hiddenContact);
        _repository.GetByClientIdAsync(_hiddenClientId, Arg.Any<CancellationToken>())
            .Returns(new List<MessengerContact> { _hiddenContact });

        _sut = new MessengerContactController(_repository, _unitOfWork, _clientVisibility);
    }

    [Test]
    public async Task GetByClient_HiddenClient_AnsweredLikeClientWithoutContacts()
    {
        var result = await _sut.GetByClient(_hiddenClientId, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBeAssignableTo<IEnumerable<MessengerContactDto>>()!.ShouldBeEmpty();
        await _repository.DidNotReceiveWithAnyArgs().GetByClientIdAsync(default, default);
    }

    [Test]
    public async Task GetById_ContactOfHiddenClient_NotFound()
    {
        var result = await _sut.GetById(_hiddenContact.Id, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public async Task Create_ForHiddenClient_NotFound_NothingWritten()
    {
        var dto = new CreateMessengerContactDto
        {
            ClientId = _hiddenClientId,
            Type = MessengerType.Telegram,
            Value = AttackerChatId,
        };

        var result = await _sut.Create(dto, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Create_ForVisibleClient_IsWritten()
    {
        var dto = new CreateMessengerContactDto
        {
            ClientId = _visibleClientId,
            Type = MessengerType.Telegram,
            Value = AttackerChatId,
        };

        var result = await _sut.Create(dto, CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
        await _repository.Received(1).AddAsync(
            Arg.Is<MessengerContact>(c => c.ClientId == _visibleClientId && c.Value == AttackerChatId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Update_ContactOfHiddenClient_NotFound_NothingWritten()
    {
        var dto = new CreateMessengerContactDto
        {
            ClientId = _visibleClientId,
            Type = MessengerType.Telegram,
            Value = AttackerChatId,
        };

        var result = await _sut.Update(_hiddenContact.Id, dto, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
        _hiddenContact.Value.ShouldBe(ExistingChatId);
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Update_VisibleContact_KeepsItsOwnerEvenWhenTheBodyNamesAnotherClient()
    {
        var visibleContact = new MessengerContact
        {
            Id = Guid.NewGuid(),
            ClientId = _visibleClientId,
            Type = MessengerType.Telegram,
            Value = ExistingChatId,
        };
        _repository.GetByIdAsync(visibleContact.Id, Arg.Any<CancellationToken>()).Returns(visibleContact);
        var dto = new CreateMessengerContactDto
        {
            ClientId = _hiddenClientId,
            Type = MessengerType.Telegram,
            Value = AttackerChatId,
        };

        var result = await _sut.Update(visibleContact.Id, dto, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        visibleContact.ClientId.ShouldBe(_visibleClientId);
    }

    [Test]
    public async Task Delete_ContactOfHiddenClient_NotFound_NothingDeleted()
    {
        var result = await _sut.Delete(_hiddenContact.Id, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        await _repository.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase(nameof(MessengerContactController.Create))]
    [TestCase(nameof(MessengerContactController.Update))]
    [TestCase(nameof(MessengerContactController.Delete))]
    public void WriteRoutes_RequireAdminOrSupervisor(string actionName)
    {
        var roles = typeof(MessengerContactController)
            .GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Roles)
            .Single(r => r != null)!
            .Split(',');

        roles.ShouldBe(new[] { MessagingConstants.RoleAdmin, MessagingConstants.RoleSupervisor }, ignoreOrder: true);
        MessagingConstants.RoleSupervisor.ShouldBe(Klacks.Api.Domain.Constants.Roles.Authorised);
        MessagingConstants.RoleAdmin.ShouldBe(Klacks.Api.Domain.Constants.Roles.Admin);
    }
}
