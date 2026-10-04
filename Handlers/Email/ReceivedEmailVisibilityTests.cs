// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the received-email endpoints ignored group visibility: a group-restricted user could list,
/// read, translate, move and delete the emails of any employee, read the email list of any client or group, and
/// see every client with emails in the group tree. An email whose sender belongs only to hidden clients must now
/// be answered exactly like a missing email, and nothing may be written for it. Since 2026-10-04 the same holds
/// when a visible client shares the sender address with a hidden one: otherwise putting a hidden employee's
/// address on a visible client exposed that employee's past and future mail.
/// </summary>

using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Translation;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class ReceivedEmailVisibilityTests
{
    private const string HiddenSender = "hidden.employee@example.com";
    private const string VisibleSender = "visible.employee@example.com";
    private const string TrashFolder = "Trash";
    private const string TargetFolder = "Archive";
    private const string TargetLanguage = "en";
    private const string UserId = "user";
    private const int Skip = 0;
    private const int Take = 50;
    private const int TreeEmailCount = 2;

    private IReceivedEmailRepository _repository = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IImapEmailService _imapService = null!;
    private IEmailQueryRepository _emailQueryRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private Guid _hiddenClientId;
    private ReceivedEmail _email = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _imapService = Substitute.For<IImapEmailService>();
        _hiddenClientId = Guid.NewGuid();
        _emailQueryRepository = EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId);
        _clientVisibilityGuard = EmailVisibilityTestDoubles.ClientsHidden(_hiddenClientId);

        _email = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            ImapUid = 7,
            Folder = TrashFolder,
            FromAddress = HiddenSender.ToUpperInvariant(),
            Subject = "Sick note",
        };
        _repository.GetByIdAsync(_email.Id).Returns(_email);
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns(TrashFolder);
        _folderRepository.GetAllAsync().Returns(new List<EmailFolder>());
    }

    [Test]
    public async Task GetById_HiddenSender_AnsweredLikeMissingEmail()
    {
        var handler = new GetReceivedEmailQueryHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetReceivedEmailQueryHandler>>());

        var result = await handler.Handle(new GetReceivedEmailQuery(_email.Id), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetById_SenderSharedByHiddenAndVisibleClient_AnsweredLikeMissingEmail()
    {
        var visibleClientId = Guid.NewGuid();
        var handler = new GetReceivedEmailQueryHandler(
            _repository,
            EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId, visibleClientId),
            _clientVisibilityGuard,
            new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetReceivedEmailQueryHandler>>());

        var result = await handler.Handle(new GetReceivedEmailQuery(_email.Id), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task List_SenderSharedByHiddenAndVisibleClient_IsExcluded()
    {
        _repository.GetFilteredListAsync(
                Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new List<ReceivedEmail>());
        var handler = new GetReceivedEmailsQueryHandler(
            _repository,
            EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId, Guid.NewGuid()),
            _clientVisibilityGuard,
            new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetReceivedEmailsQueryHandler>>());

        await handler.Handle(new GetReceivedEmailsQuery(Skip, Take, null, null, null), CancellationToken.None);

        await _repository.Received(1).GetFilteredListAsync(
            Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<string>?>(a => a != null && a.Single() == HiddenSender));
    }

    [Test]
    public async Task ByClient_VisibleClientSharesAddressWithHiddenClient_SharedAddressNotQueried()
    {
        var visibleClientId = Guid.NewGuid();
        var emailQueryRepository = EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId, visibleClientId);
        emailQueryRepository.GetEmailAddressesByClientAsync(visibleClientId, Arg.Any<CancellationToken>())
            .Returns(new List<string> { HiddenSender, VisibleSender });
        emailQueryRepository.GetEmailsByAddressesAsync(
                Arg.Any<string>(), Arg.Any<List<string>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedEmailQueryResult());
        var handler = new GetEmailsByClientQueryHandler(
            emailQueryRepository, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetEmailsByClientQueryHandler>>());

        await handler.Handle(new GetEmailsByClientQuery(visibleClientId, Skip, Take), CancellationToken.None);

        await emailQueryRepository.Received(1).GetEmailsByAddressesAsync(
            EmailConstants.ClientAssignedFolder,
            Arg.Is<List<string>>(a => a.Single() == VisibleSender),
            Skip, Take, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ByClient_OnlyAddressSharedWithHiddenClient_AnsweredLikeClientWithoutEmails()
    {
        var visibleClientId = Guid.NewGuid();
        var emailQueryRepository = EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId, visibleClientId);
        emailQueryRepository.GetEmailAddressesByClientAsync(visibleClientId, Arg.Any<CancellationToken>())
            .Returns(new List<string> { HiddenSender });
        var handler = new GetEmailsByClientQueryHandler(
            emailQueryRepository, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetEmailsByClientQueryHandler>>());

        var result = await handler.Handle(new GetEmailsByClientQuery(visibleClientId, Skip, Take), CancellationToken.None);

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        await emailQueryRepository.DidNotReceiveWithAnyArgs()
            .GetEmailsByAddressesAsync(default!, default!, default, default, default);
    }

    [Test]
    public async Task ByGroup_MemberSharesAddressWithHiddenClient_SharedAddressNotQueried()
    {
        var visibleGroupId = Guid.NewGuid();
        var visibleClientId = Guid.NewGuid();
        var groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        groupVisibilityGuard.IsGroupVisibleAsync(visibleGroupId, Arg.Any<CancellationToken>()).Returns(true);
        var groupHierarchyService = Substitute.For<IGroupHierarchyService>();
        groupHierarchyService.GetDescendantsAsync(visibleGroupId, true)
            .Returns(new List<Group> { new() { Id = visibleGroupId } });
        var emailQueryRepository = EmailVisibilityTestDoubles.SenderOwnedBy(HiddenSender, _hiddenClientId, visibleClientId);
        emailQueryRepository.GetClientIdsByGroupIdsAsync(Arg.Any<HashSet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { visibleClientId });
        emailQueryRepository.GetEmailAddressesByClientIdsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { HiddenSender, VisibleSender });
        emailQueryRepository.GetEmailsByAddressesAsync(
                Arg.Any<string>(), Arg.Any<List<string>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedEmailQueryResult());
        var handler = new GetEmailsByGroupQueryHandler(
            groupHierarchyService, emailQueryRepository, groupVisibilityGuard, _clientVisibilityGuard,
            new ReceivedEmailMapper(), Substitute.For<ILogger<GetEmailsByGroupQueryHandler>>());

        await handler.Handle(new GetEmailsByGroupQuery(visibleGroupId, Skip, Take), CancellationToken.None);

        await emailQueryRepository.Received(1).GetEmailsByAddressesAsync(
            EmailConstants.ClientAssignedFolder,
            Arg.Is<List<string>>(a => a.Single() == VisibleSender),
            Skip, Take, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Translate_HiddenSender_AnsweredLikeMissingEmail_NothingTranslated()
    {
        var translationService = Substitute.For<ITranslationService>();
        var handler = new TranslateReceivedEmailQueryHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, translationService,
            Substitute.For<ILogger<TranslateReceivedEmailQueryHandler>>());

        var result = await handler.Handle(new TranslateReceivedEmailQuery(_email.Id, TargetLanguage), CancellationToken.None);

        result.ShouldBeNull();
        await translationService.DidNotReceiveWithAnyArgs().TranslateAsync(default!, default, default!, default);
    }

    [Test]
    public async Task MarkAsRead_HiddenSender_RefusedLikeMissingEmail_NothingWritten()
    {
        var notificationService = Substitute.For<IEmailNotificationService>();
        var handler = new MarkEmailAsReadCommandHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, _unitOfWork, notificationService, _imapService,
            Substitute.For<ILogger<MarkEmailAsReadCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new MarkEmailAsReadCommand(_email.Id, true, UserId), CancellationToken.None));

        ex.Message.ShouldBe($"Email with id {_email.Id} not found.");
        _email.IsRead.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<ReceivedEmail>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_HiddenSender_RefusedLikeMissingEmail_NothingMoved()
    {
        var handler = new DeleteReceivedEmailCommandHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, _folderRepository, _unitOfWork, _imapService,
            Substitute.For<ILogger<DeleteReceivedEmailCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new DeleteReceivedEmailCommand(_email.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Email with id {_email.Id} not found.");
        await AssertNothingMovedAsync();
    }

    [Test]
    public async Task Restore_HiddenSender_RefusedLikeMissingEmail_NothingMoved()
    {
        var handler = new RestoreEmailCommandHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, _folderRepository, _unitOfWork, _imapService,
            Substitute.For<ILogger<RestoreEmailCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new RestoreEmailCommand(_email.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Email with id {_email.Id} not found.");
        await AssertNothingMovedAsync();
    }

    [Test]
    public async Task MoveToFolder_HiddenSender_RefusedLikeMissingEmail_NothingMoved()
    {
        var handler = new MoveEmailToFolderCommandHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, _unitOfWork, _imapService,
            Substitute.For<ILogger<MoveEmailToFolderCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new MoveEmailToFolderCommand(_email.Id, TargetFolder), CancellationToken.None));

        ex.Message.ShouldBe($"Email with id {_email.Id} not found.");
        await AssertNothingMovedAsync();
    }

    [Test]
    public async Task PermanentlyDelete_HiddenSender_RefusedLikeMissingEmail_NothingDeleted()
    {
        var handler = new PermanentlyDeleteEmailCommandHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, _folderRepository, _unitOfWork, _imapService,
            Substitute.For<ILogger<PermanentlyDeleteEmailCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new PermanentlyDeleteEmailCommand(_email.Id), CancellationToken.None));

        ex.Message.ShouldBe($"Email with id {_email.Id} not found.");
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _imapService.DidNotReceiveWithAnyArgs().DeleteEmailOnImapAsync(default, default!, default);
    }

    [Test]
    public async Task List_PassesSendersOfHiddenClientsAsExclusionToPageAndCounters()
    {
        _repository.GetFilteredListAsync(
                Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new List<ReceivedEmail>());
        var handler = new GetReceivedEmailsQueryHandler(
            _repository, _emailQueryRepository, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetReceivedEmailsQueryHandler>>());

        await handler.Handle(
            new GetReceivedEmailsQuery(Skip, Take, EmailConstants.ClientAssignedFolder, null, null),
            CancellationToken.None);

        await _repository.Received(1).GetFilteredListAsync(
            Arg.Is(EmailConstants.ClientAssignedFolder), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Is(Skip), Arg.Is(Take),
            Arg.Is<IReadOnlyCollection<string>?>(a => a != null && a.Single() == HiddenSender));
        await _repository.Received(2).GetFilteredCountAsync(
            Arg.Is(EmailConstants.ClientAssignedFolder), Arg.Any<bool?>(),
            Arg.Is<IReadOnlyCollection<string>?>(a => a != null && a.Single() == HiddenSender));
    }

    [Test]
    public async Task List_NoHiddenClient_PassesNoExclusion()
    {
        _repository.GetFilteredListAsync(
                Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new List<ReceivedEmail>());
        var handler = new GetReceivedEmailsQueryHandler(
            _repository, _emailQueryRepository, EmailVisibilityTestDoubles.AllClientsVisible(), new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetReceivedEmailsQueryHandler>>());

        await handler.Handle(new GetReceivedEmailsQuery(Skip, Take, null, null, null), CancellationToken.None);

        await _repository.Received(1).GetFilteredListAsync(
            Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<string>?>(a => a == null));
    }

    [Test]
    public async Task ByClient_HiddenClient_AnsweredLikeClientWithoutEmails()
    {
        var handler = new GetEmailsByClientQueryHandler(
            _emailQueryRepository, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetEmailsByClientQueryHandler>>());

        var result = await handler.Handle(new GetEmailsByClientQuery(_hiddenClientId, Skip, Take), CancellationToken.None);

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.UnreadCount.ShouldBe(0);
        await _emailQueryRepository.DidNotReceiveWithAnyArgs().GetEmailAddressesByClientAsync(default, default);
        await _emailQueryRepository.DidNotReceiveWithAnyArgs()
            .GetEmailsByAddressesAsync(default!, default!, default, default, default);
    }

    [Test]
    public async Task ByGroup_HiddenGroup_AnsweredLikeGroupWithoutEmails()
    {
        var hiddenGroupId = Guid.NewGuid();
        var groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        groupVisibilityGuard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);
        var groupHierarchyService = Substitute.For<IGroupHierarchyService>();
        var handler = new GetEmailsByGroupQueryHandler(
            groupHierarchyService, _emailQueryRepository, groupVisibilityGuard, _clientVisibilityGuard, new ReceivedEmailMapper(),
            Substitute.For<ILogger<GetEmailsByGroupQueryHandler>>());

        var result = await handler.Handle(new GetEmailsByGroupQuery(hiddenGroupId, Skip, Take), CancellationToken.None);

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.UnreadCount.ShouldBe(0);
        await groupHierarchyService.DidNotReceiveWithAnyArgs().GetDescendantsAsync(default, default);
        await _emailQueryRepository.DidNotReceiveWithAnyArgs()
            .GetEmailsByAddressesAsync(default!, default!, default, default, default);
    }

    [Test]
    public async Task GroupTree_HiddenClient_IsLeftOutWithItsCounters()
    {
        var visibleClientId = Guid.NewGuid();
        var emailQueryRepository = Substitute.For<IEmailQueryRepository>();
        emailQueryRepository
            .GetDistinctAssignedEmailAddressesAsync(EmailConstants.ClientAssignedFolder, Arg.Any<CancellationToken>())
            .Returns(new List<string> { HiddenSender, VisibleSender });
        emailQueryRepository.GetClientsWithEmailCommunicationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ClientEmailInfo>
            {
                new() { ClientId = _hiddenClientId, EmailAddress = HiddenSender, ClientDisplayName = "Hidden" },
                new() { ClientId = visibleClientId, EmailAddress = VisibleSender, ClientDisplayName = "Visible" },
            });
        emailQueryRepository
            .CountEmailsByAddressAsync(EmailConstants.ClientAssignedFolder, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TreeEmailCount);
        var groupHierarchyService = Substitute.For<IGroupHierarchyService>();
        groupHierarchyService.GetTreeAsync().Returns(new List<Group>());
        var handler = new GetEmailGroupTreeQueryHandler(
            groupHierarchyService, emailQueryRepository, _clientVisibilityGuard,
            Substitute.For<ILogger<GetEmailGroupTreeQueryHandler>>());

        var result = await handler.Handle(new GetEmailGroupTreeQuery(), CancellationToken.None);

        result.SelectMany(n => n.Children).Select(c => c.Id).ShouldBe(new[] { visibleClientId });
        result.Sum(n => n.EmailCount).ShouldBe(TreeEmailCount);
    }

    private async Task AssertNothingMovedAsync()
    {
        await _repository.DidNotReceive().MoveToFolderAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _imapService.DidNotReceiveWithAnyArgs().MoveEmailOnImapAsync(default, default!, default!, default);
    }
}
