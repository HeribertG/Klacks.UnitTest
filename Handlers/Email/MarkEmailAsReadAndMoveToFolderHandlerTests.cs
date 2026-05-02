// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;
using NSubstitute;
using IEmailNotificationService = Klacks.Api.Domain.Interfaces.Email.IEmailNotificationService;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class MarkEmailAsReadCommandHandlerTests
{
    private IReceivedEmailRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IEmailNotificationService _notificationService = null!;
    private IImapEmailService _imapService = null!;
    private MarkEmailAsReadCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _notificationService = Substitute.For<IEmailNotificationService>();
        _imapService = Substitute.For<IImapEmailService>();

        var logger = Substitute.For<ILogger<MarkEmailAsReadCommandHandler>>();
        _handler = new MarkEmailAsReadCommandHandler(
            _repository, _unitOfWork, _notificationService, _imapService, logger);
    }

    [Test]
    public async Task Handle_ExistingEmail_SetsReadAndCallsImapAndNotification()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 42,
            Folder = "INBOX",
            IsRead = false
        };
        _repository.GetByIdAsync(emailId).Returns(email);

        var result = await _handler.Handle(
            new MarkEmailAsReadCommand(emailId, true), CancellationToken.None);

        result.ShouldBeTrue();
        email.IsRead.ShouldBeTrue();
        await _repository.Received(1).UpdateAsync(email);
        await _unitOfWork.Received(1).CompleteAsync();
        await _imapService.Received(1).SetReadFlagOnImapAsync(42, "INBOX", true, Arg.Any<CancellationToken>());
        await _notificationService.Received(1).NotifyReadStateChangedAsync(emailId, true, "INBOX");
    }

    [Test]
    public async Task Handle_NonExistingEmail_ThrowsKeyNotFoundException()
    {
        var emailId = Guid.NewGuid();
        _repository.GetByIdAsync(emailId).Returns((ReceivedEmail?)null);

        Func<Task> act = async () => await _handler.Handle(
            new MarkEmailAsReadCommand(emailId, true), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<ReceivedEmail>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_MarkAsUnread_CallsImapWithFalse()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 99,
            Folder = "INBOX",
            IsRead = true
        };
        _repository.GetByIdAsync(emailId).Returns(email);

        await _handler.Handle(
            new MarkEmailAsReadCommand(emailId, false), CancellationToken.None);

        email.IsRead.ShouldBeFalse();
        await _imapService.Received(1).SetReadFlagOnImapAsync(99, "INBOX", false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Success_CallsImapWithCorrectFolderAndUid()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 1337,
            Folder = "Sent",
            IsRead = false
        };
        _repository.GetByIdAsync(emailId).Returns(email);

        await _handler.Handle(
            new MarkEmailAsReadCommand(emailId, true), CancellationToken.None);

        await _imapService.Received(1).SetReadFlagOnImapAsync(1337, "Sent", true, Arg.Any<CancellationToken>());
        await _notificationService.Received(1).NotifyReadStateChangedAsync(emailId, true, "Sent");
    }
}

[TestFixture]
public class MoveEmailToFolderCommandHandlerTests
{
    private IReceivedEmailRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IImapEmailService _imapService = null!;
    private MoveEmailToFolderCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _imapService = Substitute.For<IImapEmailService>();

        var logger = Substitute.For<ILogger<MoveEmailToFolderCommandHandler>>();
        _handler = new MoveEmailToFolderCommandHandler(
            _repository, _unitOfWork, _imapService, logger);
    }

    [Test]
    public async Task Handle_ExistingEmail_MovesAndCallsImap()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 55,
            Folder = "INBOX"
        };
        _repository.GetByIdAsync(emailId).Returns(email);

        var result = await _handler.Handle(
            new MoveEmailToFolderCommand(emailId, "Archive"), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).MoveToFolderAsync(emailId, "Archive");
        await _unitOfWork.Received(1).CompleteAsync();
        await _imapService.Received(1).MoveEmailOnImapAsync(55, "INBOX", "Archive", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NonExistingEmail_ThrowsKeyNotFoundException()
    {
        var emailId = Guid.NewGuid();
        _repository.GetByIdAsync(emailId).Returns((ReceivedEmail?)null);

        Func<Task> act = async () => await _handler.Handle(
            new MoveEmailToFolderCommand(emailId, "Archive"), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_Success_CallsImapWithPreviousAndTargetFolder()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 200,
            Folder = "Drafts"
        };
        _repository.GetByIdAsync(emailId).Returns(email);

        await _handler.Handle(
            new MoveEmailToFolderCommand(emailId, "Trash"), CancellationToken.None);

        await _imapService.Received(1).MoveEmailOnImapAsync(200, "Drafts", "Trash", Arg.Any<CancellationToken>());
    }
}
