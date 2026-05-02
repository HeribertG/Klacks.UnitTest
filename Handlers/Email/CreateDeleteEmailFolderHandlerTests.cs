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
public class CreateEmailFolderCommandHandlerTests
{
    private IEmailFolderRepository _folderRepository = null!;
    private IImapEmailService _imapService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CreateEmailFolderCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _imapService = Substitute.For<IImapEmailService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<CreateEmailFolderCommandHandler>>();
        _handler = new CreateEmailFolderCommandHandler(
            _folderRepository, _imapService, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_NewFolder_CreatesOnImapAndInDb()
    {
        _folderRepository.ExistsByImapNameAsync("TestFolder").Returns(false);
        _folderRepository.GetAllAsync().Returns([]);

        var result = await _handler.Handle(
            new CreateEmailFolderCommand("Test", "TestFolder"), CancellationToken.None);

        result.ShouldNotBeNull();
        await _imapService.Received(1).CreateFolderOnImapAsync("TestFolder", Arg.Any<CancellationToken>());
        await _folderRepository.Received(1).AddAsync(Arg.Any<EmailFolder>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NewFolder_CallsImapBeforeDbSave()
    {
        _folderRepository.ExistsByImapNameAsync("MyFolder").Returns(false);
        _folderRepository.GetAllAsync().Returns([]);

        var callOrder = new List<string>();
        _imapService.CreateFolderOnImapAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("imap"));
        _folderRepository.AddAsync(Arg.Any<EmailFolder>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("db"));

        await _handler.Handle(
            new CreateEmailFolderCommand("My Folder", "MyFolder"), CancellationToken.None);

        callOrder.ShouldBe(new[] {"imap", "db"});
    }

    [Test]
    public async Task Handle_ExistingFolder_ThrowsInvalidOperationException()
    {
        _folderRepository.ExistsByImapNameAsync("Existing").Returns(true);

        Func<Task> act = async () => await _handler.Handle(
            new CreateEmailFolderCommand("Existing", "Existing"), CancellationToken.None);

        await act.ShouldThrowAsync<InvalidRequestException>();
        await _imapService.DidNotReceive().CreateFolderOnImapAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _folderRepository.DidNotReceive().AddAsync(Arg.Any<EmailFolder>());
    }

    [Test]
    public async Task Handle_NewFolder_SetsIsSystemFalse()
    {
        _folderRepository.ExistsByImapNameAsync("Custom").Returns(false);
        _folderRepository.GetAllAsync().Returns([]);

        EmailFolder? capturedFolder = null;
        _folderRepository.AddAsync(Arg.Do<EmailFolder>(f => capturedFolder = f))
            .Returns(Task.CompletedTask);

        await _handler.Handle(
            new CreateEmailFolderCommand("Custom", "Custom"), CancellationToken.None);

        capturedFolder.ShouldNotBeNull();
        capturedFolder!.IsSystem.ShouldBeFalse();
        capturedFolder.ImapFolderName.ShouldBe("Custom");
    }
}

[TestFixture]
public class DeleteEmailFolderCommandHandlerTests
{
    private IEmailFolderRepository _folderRepository = null!;
    private IReceivedEmailRepository _emailRepository = null!;
    private IImapEmailService _imapService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteEmailFolderCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _emailRepository = Substitute.For<IReceivedEmailRepository>();
        _imapService = Substitute.For<IImapEmailService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<DeleteEmailFolderCommandHandler>>();
        _handler = new DeleteEmailFolderCommandHandler(
            _folderRepository, _emailRepository, _imapService, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_NonSystemFolder_DeletesOnImapAndInDb()
    {
        var folderId = Guid.NewGuid();
        var folder = new EmailFolder
        {
            Id = folderId,
            Name = "Custom",
            ImapFolderName = "Custom",
            IsSystem = false
        };
        _folderRepository.GetByIdAsync(folderId).Returns(folder);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");

        var result = await _handler.Handle(
            new DeleteEmailFolderCommand(folderId), CancellationToken.None);

        result.ShouldBeTrue();
        await _imapService.Received(1).DeleteFolderOnImapAsync("Custom", Arg.Any<CancellationToken>());
        await _folderRepository.Received(1).DeleteAsync(folderId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NonSystemFolder_MovesEmailsToTrashBeforeImapDelete()
    {
        var folderId = Guid.NewGuid();
        var folder = new EmailFolder
        {
            Id = folderId,
            Name = "Custom",
            ImapFolderName = "Custom",
            IsSystem = false
        };
        _folderRepository.GetByIdAsync(folderId).Returns(folder);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Trash");

        var callOrder = new List<string>();
        _emailRepository.BulkMoveFolderAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(0)
            .AndDoes(_ => callOrder.Add("moveEmails"));
        _imapService.DeleteFolderOnImapAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("imapDelete"));

        await _handler.Handle(new DeleteEmailFolderCommand(folderId), CancellationToken.None);

        callOrder.ShouldBe(new[] {"moveEmails", "imapDelete"});
        await _emailRepository.Received(1).BulkMoveFolderAsync("Custom", "Trash");
    }

    [Test]
    public async Task Handle_SystemFolder_ThrowsInvalidOperationException()
    {
        var folderId = Guid.NewGuid();
        var folder = new EmailFolder
        {
            Id = folderId,
            Name = "INBOX",
            ImapFolderName = "INBOX",
            IsSystem = true
        };
        _folderRepository.GetByIdAsync(folderId).Returns(folder);

        Func<Task> act = async () => await _handler.Handle(
            new DeleteEmailFolderCommand(folderId), CancellationToken.None);

        await act.ShouldThrowAsync<InvalidRequestException>();
        await _imapService.DidNotReceive().DeleteFolderOnImapAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _folderRepository.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
    }

    [Test]
    public async Task Handle_FolderNotFound_ReturnsFalse()
    {
        var folderId = Guid.NewGuid();
        _folderRepository.GetByIdAsync(folderId).Returns((EmailFolder?)null);

        var result = await _handler.Handle(
            new DeleteEmailFolderCommand(folderId), CancellationToken.None);

        result.ShouldBeFalse();
        await _imapService.DidNotReceive().DeleteFolderOnImapAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
