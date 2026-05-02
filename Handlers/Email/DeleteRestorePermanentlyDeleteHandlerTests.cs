// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class DeleteReceivedEmailCommandHandlerTests
{
    private IReceivedEmailRepository _repository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IImapEmailService _imapService = null!;
    private DeleteReceivedEmailCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _imapService = Substitute.For<IImapEmailService>();

        var logger = Substitute.For<ILogger<DeleteReceivedEmailCommandHandler>>();
        _handler = new DeleteReceivedEmailCommandHandler(
            _repository, _folderRepository, _unitOfWork, _imapService, logger);
    }

    [Test]
    public async Task Handle_ExistingEmail_MovesToTrashAndCallsImap()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 42,
            Folder = "INBOX"
        };
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");
        _repository.GetByIdAsync(emailId).Returns(email);

        var result = await _handler.Handle(
            new DeleteReceivedEmailCommand(emailId), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).MoveToFolderAsync(emailId, "Trash");
        await _unitOfWork.Received(1).CompleteAsync();
        await _imapService.Received(1).MoveEmailOnImapAsync(42, "INBOX", "Trash", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoTrashFolder_ThrowsInvalidRequestException()
    {
        var emailId = Guid.NewGuid();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns((string?)null);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteReceivedEmailCommand(emailId), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("No trash folder configured.");
        await _repository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmailNotFound_ThrowsKeyNotFoundException()
    {
        var emailId = Guid.NewGuid();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");
        _repository.GetByIdAsync(emailId).Returns((ReceivedEmail?)null);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteReceivedEmailCommand(emailId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_Success_CallsImapWithPreviousFolderAndTrash()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 1337,
            Folder = "Sent"
        };
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");
        _repository.GetByIdAsync(emailId).Returns(email);

        await _handler.Handle(
            new DeleteReceivedEmailCommand(emailId), CancellationToken.None);

        await _imapService.Received(1).MoveEmailOnImapAsync(1337, "Sent", "Trash", Arg.Any<CancellationToken>());
    }
}

[TestFixture]
public class RestoreEmailCommandHandlerTests
{
    private IReceivedEmailRepository _repository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IImapEmailService _imapService = null!;
    private RestoreEmailCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _imapService = Substitute.For<IImapEmailService>();

        var logger = Substitute.For<ILogger<RestoreEmailCommandHandler>>();
        _handler = new RestoreEmailCommandHandler(
            _repository, _folderRepository, _unitOfWork, _imapService, logger);
    }

    [Test]
    public async Task Handle_ExistingEmail_RestoresToNonTrashFolderAndCallsImap()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 55,
            Folder = "Trash"
        };
        var folders = new List<EmailFolder>
        {
            new() { Name = "Inbox", ImapFolderName = "INBOX", SpecialUse = FolderSpecialUse.Inbox },
            new() { Name = "Trash", ImapFolderName = "Trash", SpecialUse = FolderSpecialUse.Trash }
        };
        _folderRepository.GetAllAsync().Returns(folders);
        _repository.GetByIdAsync(emailId).Returns(email);

        var result = await _handler.Handle(
            new RestoreEmailCommand(emailId), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).MoveToFolderAsync(emailId, "INBOX");
        await _unitOfWork.Received(1).CompleteAsync();
        await _imapService.Received(1).MoveEmailOnImapAsync(55, "Trash", "INBOX", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_EmailNotFound_ThrowsKeyNotFoundException()
    {
        var emailId = Guid.NewGuid();
        _folderRepository.GetAllAsync().Returns(new List<EmailFolder>
        {
            new() { Name = "Inbox", ImapFolderName = "INBOX", SpecialUse = FolderSpecialUse.Inbox }
        });
        _repository.GetByIdAsync(emailId).Returns((ReceivedEmail?)null);

        Func<Task> act = async () => await _handler.Handle(
            new RestoreEmailCommand(emailId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_NoNonTrashFolder_UsesInboxDefault()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 77,
            Folder = "Trash"
        };
        var folders = new List<EmailFolder>
        {
            new() { Name = "Trash", ImapFolderName = "Trash", SpecialUse = FolderSpecialUse.Trash }
        };
        _folderRepository.GetAllAsync().Returns(folders);
        _repository.GetByIdAsync(emailId).Returns(email);

        await _handler.Handle(
            new RestoreEmailCommand(emailId), CancellationToken.None);

        await _repository.Received(1).MoveToFolderAsync(emailId, "INBOX");
        await _imapService.Received(1).MoveEmailOnImapAsync(77, "Trash", "INBOX", Arg.Any<CancellationToken>());
    }
}

[TestFixture]
public class PermanentlyDeleteEmailCommandHandlerTests
{
    private IReceivedEmailRepository _repository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IImapEmailService _imapService = null!;
    private PermanentlyDeleteEmailCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _imapService = Substitute.For<IImapEmailService>();

        var logger = Substitute.For<ILogger<PermanentlyDeleteEmailCommandHandler>>();
        _handler = new PermanentlyDeleteEmailCommandHandler(
            _repository, _folderRepository, _unitOfWork, _imapService, logger);
    }

    [Test]
    public async Task Handle_EmailInTrash_DeletesAndCallsImapDelete()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 100,
            Folder = "Trash"
        };
        _repository.GetByIdAsync(emailId).Returns(email);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");

        var result = await _handler.Handle(
            new PermanentlyDeleteEmailCommand(emailId), CancellationToken.None);

        result.ShouldBeTrue();
        await _repository.Received(1).DeleteAsync(emailId);
        await _unitOfWork.Received(1).CompleteAsync();
        await _imapService.Received(1).DeleteEmailOnImapAsync(100, "Trash", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_EmailNotFound_ThrowsKeyNotFoundException()
    {
        var emailId = Guid.NewGuid();
        _repository.GetByIdAsync(emailId).Returns((ReceivedEmail?)null);

        Func<Task> act = async () => await _handler.Handle(
            new PermanentlyDeleteEmailCommand(emailId), CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmailNotInTrash_ThrowsInvalidRequestException()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 200,
            Folder = "INBOX"
        };
        _repository.GetByIdAsync(emailId).Returns(email);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");

        Func<Task> act = async () => await _handler.Handle(
            new PermanentlyDeleteEmailCommand(emailId), CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("Only emails in trash can be permanently deleted.");
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_Success_CallsImapDeleteWithCorrectUidAndFolder()
    {
        var emailId = Guid.NewGuid();
        var email = new ReceivedEmail
        {
            Id = emailId,
            ImapUid = 999,
            Folder = "Trash"
        };
        _repository.GetByIdAsync(emailId).Returns(email);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");

        await _handler.Handle(
            new PermanentlyDeleteEmailCommand(emailId), CancellationToken.None);

        await _imapService.Received(1).DeleteEmailOnImapAsync(999, "Trash", Arg.Any<CancellationToken>());
    }
}
