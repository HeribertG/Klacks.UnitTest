// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class CreateSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IReceivedEmailRepository _emailRepository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private ISpamFilterService _spamFilterService = null!;
    private IImapEmailService _imapService = null!;
    private IEmailNotificationService _notificationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CreateSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _emailRepository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _spamFilterService = Substitute.For<ISpamFilterService>();
        _imapService = Substitute.For<IImapEmailService>();
        _notificationService = Substitute.For<IEmailNotificationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<CreateSpamRuleCommandHandler>>();
        _handler = new CreateSpamRuleCommandHandler(
            _repository, _emailRepository, _folderRepository,
            _spamFilterService, _imapService, _notificationService,
            _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_NewRule_CreatesRuleAndReclassifiesInbox()
    {
        _repository.GetAllAsync().Returns(new List<SpamRule>());
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var spamEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 10,
            Folder = "INBOX",
            FromAddress = "spam@test.com"
        };
        _emailRepository.GetListByFolderAsync("INBOX", 0, int.MaxValue).Returns([spamEmail]);
        _emailRepository.GetListByFolderAsync(EmailConstants.ClientAssignedFolder, 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _spamFilterService.ClassifyAsync(spamEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        var result = await _handler.Handle(
            new CreateSpamRuleCommand(SpamRuleType.SenderContains, "spam@test.com"), CancellationToken.None);

        result.Should().NotBeNull();
        await _repository.Received(1).AddAsync(Arg.Any<SpamRule>());
        await _emailRepository.Received(1).MoveToFolderAsync(spamEmail.Id, "Junk");
        await _imapService.Received(1).MoveEmailOnImapAsync(10, "INBOX", "Junk", Arg.Any<CancellationToken>());
        await _notificationService.Received(1).NotifyNewEmailsAsync(1);
    }

    [Test]
    public async Task Handle_NewRule_NoSpamFound_NoNotification()
    {
        _repository.GetAllAsync().Returns(new List<SpamRule>());
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var cleanEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 20,
            Folder = "INBOX",
            FromAddress = "good@test.com"
        };
        _emailRepository.GetListByFolderAsync("INBOX", 0, int.MaxValue).Returns([cleanEmail]);
        _emailRepository.GetListByFolderAsync(EmailConstants.ClientAssignedFolder, 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _spamFilterService.ClassifyAsync(cleanEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });

        await _handler.Handle(
            new CreateSpamRuleCommand(SpamRuleType.SenderContains, "other@test.com"), CancellationToken.None);

        await _emailRepository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _notificationService.DidNotReceive().NotifyNewEmailsAsync(Arg.Any<int>());
    }

    [Test]
    public async Task Handle_NewRule_AlsoChecksClientAssignedEmails()
    {
        _repository.GetAllAsync().Returns(new List<SpamRule>());
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");
        _emailRepository.GetListByFolderAsync("INBOX", 0, int.MaxValue).Returns(new List<ReceivedEmail>());

        var clientEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 30,
            Folder = EmailConstants.ClientAssignedFolder,
            SourceImapFolder = "INBOX",
            FromAddress = "spam-client@test.com"
        };
        _emailRepository.GetListByFolderAsync(EmailConstants.ClientAssignedFolder, 0, int.MaxValue).Returns([clientEmail]);
        _spamFilterService.ClassifyAsync(clientEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        await _handler.Handle(
            new CreateSpamRuleCommand(SpamRuleType.SenderContains, "spam-client@test.com"), CancellationToken.None);

        await _emailRepository.Received(1).MoveToFolderAsync(clientEmail.Id, "Junk");
        await _imapService.Received(1).MoveEmailOnImapAsync(30, "INBOX", "Junk", Arg.Any<CancellationToken>());
    }
}

[TestFixture]
public class DeleteSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IReceivedEmailRepository _emailRepository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private ISpamFilterService _spamFilterService = null!;
    private IImapEmailService _imapService = null!;
    private IEmailClientAssignmentService _assignmentService = null!;
    private IEmailNotificationService _notificationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _emailRepository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _spamFilterService = Substitute.For<ISpamFilterService>();
        _imapService = Substitute.For<IImapEmailService>();
        _assignmentService = Substitute.For<IEmailClientAssignmentService>();
        _notificationService = Substitute.For<IEmailNotificationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<DeleteSpamRuleCommandHandler>>();
        _handler = new DeleteSpamRuleCommandHandler(
            _repository, _emailRepository, _folderRepository,
            _spamFilterService, _imapService, _assignmentService,
            _notificationService, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_DeleteRule_ReclassifiesJunkEmails()
    {
        var ruleId = Guid.NewGuid();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var junkEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 50,
            Folder = "Junk",
            SourceImapFolder = "INBOX",
            FromAddress = "restored@test.com"
        };
        _emailRepository.GetListByFolderAsync("Junk", 0, int.MaxValue).Returns([junkEmail]);
        _spamFilterService.ClassifyAsync(junkEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });

        var result = await _handler.Handle(
            new DeleteSpamRuleCommand(ruleId), CancellationToken.None);

        result.Should().BeTrue();
        await _repository.Received(1).DeleteAsync(ruleId);
        await _emailRepository.Received(1).MoveToFolderAsync(junkEmail.Id, "INBOX");
        await _imapService.Received(1).MoveEmailOnImapAsync(50, "Junk", "INBOX", Arg.Any<CancellationToken>());
        await _assignmentService.Received(1).AssignInboxEmailsToClientsAsync();
        await _notificationService.Received(1).NotifyNewEmailsAsync(1);
    }

    [Test]
    public async Task Handle_DeleteRule_StillSpam_NoMove()
    {
        var ruleId = Guid.NewGuid();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var junkEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 60,
            Folder = "Junk",
            FromAddress = "stillspam@test.com"
        };
        _emailRepository.GetListByFolderAsync("Junk", 0, int.MaxValue).Returns([junkEmail]);
        _spamFilterService.ClassifyAsync(junkEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        await _handler.Handle(new DeleteSpamRuleCommand(ruleId), CancellationToken.None);

        await _emailRepository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _assignmentService.DidNotReceive().AssignInboxEmailsToClientsAsync();
        await _notificationService.DidNotReceive().NotifyNewEmailsAsync(Arg.Any<int>());
    }

    [Test]
    public async Task Handle_DeleteRule_RestoresToSourceImapFolder()
    {
        var ruleId = Guid.NewGuid();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var junkEmail = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 70,
            Folder = "Junk",
            SourceImapFolder = "SomeOtherFolder",
            FromAddress = "user@test.com"
        };
        _emailRepository.GetListByFolderAsync("Junk", 0, int.MaxValue).Returns([junkEmail]);
        _spamFilterService.ClassifyAsync(junkEmail, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });

        await _handler.Handle(new DeleteSpamRuleCommand(ruleId), CancellationToken.None);

        await _emailRepository.Received(1).MoveToFolderAsync(junkEmail.Id, "SomeOtherFolder");
        await _imapService.Received(1).MoveEmailOnImapAsync(70, "Junk", "SomeOtherFolder", Arg.Any<CancellationToken>());
    }
}

[TestFixture]
public class UpdateSpamRuleCommandHandlerTests
{
    private ISpamRuleRepository _repository = null!;
    private IReceivedEmailRepository _emailRepository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private ISpamFilterService _spamFilterService = null!;
    private IImapEmailService _imapService = null!;
    private IEmailClientAssignmentService _assignmentService = null!;
    private IEmailNotificationService _notificationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateSpamRuleCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISpamRuleRepository>();
        _emailRepository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _spamFilterService = Substitute.For<ISpamFilterService>();
        _imapService = Substitute.For<IImapEmailService>();
        _assignmentService = Substitute.For<IEmailClientAssignmentService>();
        _notificationService = Substitute.For<IEmailNotificationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<UpdateSpamRuleCommandHandler>>();
        _handler = new UpdateSpamRuleCommandHandler(
            _repository, _emailRepository, _folderRepository,
            _spamFilterService, _imapService, _assignmentService,
            _notificationService, _unitOfWork, logger);
    }

    [Test]
    public async Task Handle_UpdateRule_ReclassifiesAllFolders()
    {
        var ruleId = Guid.NewGuid();
        var rule = new SpamRule
        {
            Id = ruleId,
            RuleType = SpamRuleType.SenderContains,
            Pattern = "old@test.com",
            IsActive = true,
            SortOrder = 1
        };
        _repository.GetByIdAsync(ruleId).Returns(rule);

        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");
        _emailRepository.GetListByFolderAsync("INBOX", 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _emailRepository.GetListByFolderAsync(EmailConstants.ClientAssignedFolder, 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _emailRepository.GetListByFolderAsync("Junk", 0, int.MaxValue).Returns(new List<ReceivedEmail>());

        var result = await _handler.Handle(
            new UpdateSpamRuleCommand(ruleId, SpamRuleType.SenderContains, "new@test.com", true, 1),
            CancellationToken.None);

        result.Should().NotBeNull();
        rule.Pattern.Should().Be("new@test.com");
        await _repository.Received(1).UpdateAsync(rule);
    }

    [Test]
    public async Task Handle_RuleNotFound_ThrowsKeyNotFoundException()
    {
        var ruleId = Guid.NewGuid();
        _repository.GetByIdAsync(ruleId).Returns((SpamRule?)null);

        Func<Task> act = async () => await _handler.Handle(
            new UpdateSpamRuleCommand(ruleId, SpamRuleType.SenderContains, "test", true, 1),
            CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<SpamRule>());
    }

    [Test]
    public async Task Handle_UpdateRule_MovesInboxSpamToJunk()
    {
        var ruleId = Guid.NewGuid();
        var rule = new SpamRule { Id = ruleId, RuleType = SpamRuleType.SenderContains, Pattern = "x", IsActive = true, SortOrder = 1 };
        _repository.GetByIdAsync(ruleId).Returns(rule);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Junk");

        var inboxSpam = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 100,
            Folder = "INBOX",
            FromAddress = "new-spam@test.com"
        };
        _emailRepository.GetListByFolderAsync("INBOX", 0, int.MaxValue).Returns([inboxSpam]);
        _emailRepository.GetListByFolderAsync(EmailConstants.ClientAssignedFolder, 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _emailRepository.GetListByFolderAsync("Junk", 0, int.MaxValue).Returns(new List<ReceivedEmail>());
        _spamFilterService.ClassifyAsync(inboxSpam, Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = true });

        await _handler.Handle(
            new UpdateSpamRuleCommand(ruleId, SpamRuleType.SenderContains, "new-spam@test.com", true, 1),
            CancellationToken.None);

        await _emailRepository.Received(1).MoveToFolderAsync(inboxSpam.Id, "Junk");
        await _imapService.Received(1).MoveEmailOnImapAsync(100, "INBOX", "Junk", Arg.Any<CancellationToken>());
        await _notificationService.Received(1).NotifyNewEmailsAsync(Arg.Any<int>());
    }
}
