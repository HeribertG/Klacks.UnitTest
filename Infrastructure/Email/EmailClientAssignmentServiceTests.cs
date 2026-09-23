// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailClientAssignmentService — verifies sender-to-client resolution via
/// Communication records (ResolveClientAsync), the inbox-to-client-folder move for newly received
/// mail (AssignNewEmailAsync), and returning orphaned client-assigned mail whose sender no longer
/// matches a known client address (ReassignOrphanedEmailsAsync). AssignInboxEmailsToClientsAsync uses
/// EF Core's ExecuteUpdateAsync, which the InMemory provider used here does not support, so that
/// method is covered only by an explicit [Ignore] placeholder documenting the gap.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailClientAssignmentServiceTests
{
    private const string InboxImapName = "INBOX";

    private string _databaseName = null!;
    private DataBaseContext _context = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private EmailClientAssignmentService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _databaseName = Guid.NewGuid().ToString();
        _context = CreateContext(_databaseName);

        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns(InboxImapName);

        _service = new EmailClientAssignmentService(
            _context, _folderRepository, Substitute.For<ILogger<EmailClientAssignmentService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private static DataBaseContext CreateContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var context = new DataBaseContext(options, httpContextAccessor);
        context.Database.EnsureCreated();
        return context;
    }

    private async Task<Communication> AddCommunicationAsync(
        string value,
        CommunicationTypeEnum type = CommunicationTypeEnum.PrivateMail,
        bool isDeleted = false)
    {
        var communication = new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Type = type,
            Value = value,
            IsDeleted = isDeleted,
        };
        _context.Set<Communication>().Add(communication);
        await _context.SaveChangesAsync();
        return communication;
    }

    private async Task<(Client Client, Communication Communication)> AddClientWithCommunicationAsync(
        string value,
        CommunicationTypeEnum type = CommunicationTypeEnum.PrivateMail,
        bool clientDeleted = false,
        bool communicationDeleted = false,
        EntityTypeEnum clientType = EntityTypeEnum.Employee)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Test Client",
            Type = clientType,
            IsDeleted = clientDeleted,
        };
        var communication = new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Client = client,
            Type = type,
            Value = value,
            IsDeleted = communicationDeleted,
        };
        _context.Set<Client>().Add(client);
        _context.Set<Communication>().Add(communication);
        await _context.SaveChangesAsync();
        return (client, communication);
    }

    private async Task<ReceivedEmail> AddReceivedEmailAsync(
        string folder, string fromAddress, string sourceImapFolder = "")
    {
        var email = new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            MessageId = Guid.NewGuid().ToString(),
            Folder = folder,
            SourceImapFolder = sourceImapFolder,
            FromAddress = fromAddress,
        };
        _context.ReceivedEmails.Add(email);
        await _context.SaveChangesAsync();
        return email;
    }

    private static ReceivedEmail Email(string fromAddress) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = Guid.NewGuid().ToString(),
        FromAddress = fromAddress,
    };

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task ResolveClientAsync_EmptyFromAddress_ReturnsNullWithoutDatabaseAccess(string? fromAddress)
    {
        var email = Email(fromAddress!);

        var result = await _service.ResolveClientAsync(email, CancellationToken.None);

        result.ShouldBeNull();
        _context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveClientAsync_PrivateMailMatch_ReturnsClientIdAndType()
    {
        var (client, _) = await AddClientWithCommunicationAsync(
            "test@example.com", CommunicationTypeEnum.PrivateMail, clientType: EntityTypeEnum.Employee);

        var result = await _service.ResolveClientAsync(Email("test@example.com"), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ClientId.ShouldBe(client.Id);
        result.Value.ClientType.ShouldBe(EntityTypeEnum.Employee);
    }

    [Test]
    public async Task ResolveClientAsync_OfficeMailMatch_ReturnsClientIdAndType()
    {
        var (client, _) = await AddClientWithCommunicationAsync(
            "office@example.com", CommunicationTypeEnum.OfficeMail, clientType: EntityTypeEnum.Customer);

        var result = await _service.ResolveClientAsync(Email("office@example.com"), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ClientId.ShouldBe(client.Id);
        result.Value.ClientType.ShouldBe(EntityTypeEnum.Customer);
    }

    [Test]
    public async Task ResolveClientAsync_CaseInsensitiveAddress_StillMatches()
    {
        var (client, _) = await AddClientWithCommunicationAsync("test@example.com");

        var result = await _service.ResolveClientAsync(Email("TEST@Example.COM"), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.ClientId.ShouldBe(client.Id);
    }

    [Test]
    public async Task ResolveClientAsync_DeletedCommunication_ReturnsNull()
    {
        await AddClientWithCommunicationAsync("test@example.com", communicationDeleted: true);

        var result = await _service.ResolveClientAsync(Email("test@example.com"), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveClientAsync_DeletedClient_ReturnsNull()
    {
        await AddClientWithCommunicationAsync("test@example.com", clientDeleted: true);

        var result = await _service.ResolveClientAsync(Email("test@example.com"), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveClientAsync_NoClientOnCommunication_ReturnsNull()
    {
        await AddCommunicationAsync("test@example.com");

        var result = await _service.ResolveClientAsync(Email("test@example.com"), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveClientAsync_AddressMismatch_ReturnsNull()
    {
        await AddClientWithCommunicationAsync("known@example.com");

        var result = await _service.ResolveClientAsync(Email("unknown@example.com"), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveClientAsync_OtherCommunicationType_DoesNotMatchEvenWithSameValue()
    {
        await AddClientWithCommunicationAsync("test@example.com", CommunicationTypeEnum.Others);

        var result = await _service.ResolveClientAsync(Email("test@example.com"), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task AssignNewEmailAsync_InboxFolderAndKnownAddress_MovesToClientAssignedFolder()
    {
        await AddCommunicationAsync("client@example.com");
        var email = Email("client@example.com");
        email.Folder = InboxImapName;

        await _service.AssignNewEmailAsync(email);

        email.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
    }

    [Test]
    public async Task AssignNewEmailAsync_InboxFolderComparisonIsCaseInsensitive()
    {
        await AddCommunicationAsync("client@example.com");
        var email = Email("client@example.com");
        email.Folder = "inbox";

        await _service.AssignNewEmailAsync(email);

        email.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
    }

    [Test]
    public async Task AssignNewEmailAsync_NotInInboxFolder_LeavesFolderUnchanged_EvenWithKnownAddress()
    {
        await AddCommunicationAsync("client@example.com");
        var email = Email("client@example.com");
        email.Folder = "Some-Other-Folder";

        await _service.AssignNewEmailAsync(email);

        email.Folder.ShouldBe("Some-Other-Folder");
    }

    [Test]
    public async Task AssignNewEmailAsync_InboxFolderButUnknownAddress_LeavesFolderUnchanged()
    {
        var email = Email("unknown@example.com");
        email.Folder = InboxImapName;

        await _service.AssignNewEmailAsync(email);

        email.Folder.ShouldBe(InboxImapName);
    }

    [Test]
    public async Task ReassignOrphanedEmailsAsync_UnknownSender_MovesToSourceImapFolder_WhenSet()
    {
        var email = await AddReceivedEmailAsync(
            EmailConstants.ClientAssignedFolder, "gone@example.com", sourceImapFolder: "Archive");

        await _service.ReassignOrphanedEmailsAsync();

        var reloaded = await CreateContext(_databaseName).ReceivedEmails.SingleAsync(e => e.Id == email.Id);
        reloaded.Folder.ShouldBe("Archive");
    }

    [Test]
    public async Task ReassignOrphanedEmailsAsync_UnknownSender_NoSourceFolder_FallsBackToResolvedInboxFolder()
    {
        var email = await AddReceivedEmailAsync(
            EmailConstants.ClientAssignedFolder, "gone@example.com", sourceImapFolder: "");

        await _service.ReassignOrphanedEmailsAsync();

        var reloaded = await CreateContext(_databaseName).ReceivedEmails.SingleAsync(e => e.Id == email.Id);
        reloaded.Folder.ShouldBe(InboxImapName);
    }

    [Test]
    public async Task ReassignOrphanedEmailsAsync_UnknownSender_NoSourceFolderAndNoResolvedInbox_FallsBackToInboxLiteral()
    {
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns((string?)null);
        var email = await AddReceivedEmailAsync(
            EmailConstants.ClientAssignedFolder, "gone@example.com", sourceImapFolder: "");

        await _service.ReassignOrphanedEmailsAsync();

        var reloaded = await CreateContext(_databaseName).ReceivedEmails.SingleAsync(e => e.Id == email.Id);
        reloaded.Folder.ShouldBe("INBOX");
    }

    [Test]
    public async Task ReassignOrphanedEmailsAsync_StillKnownSender_StaysInClientAssignedFolder()
    {
        await AddCommunicationAsync("client@example.com");
        var email = await AddReceivedEmailAsync(EmailConstants.ClientAssignedFolder, "client@example.com");

        await _service.ReassignOrphanedEmailsAsync();

        var reloaded = await CreateContext(_databaseName).ReceivedEmails.SingleAsync(e => e.Id == email.Id);
        reloaded.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
    }

    [Test]
    public async Task ReassignOrphanedEmailsAsync_EmailInOtherFolder_IsNotTouched()
    {
        var email = await AddReceivedEmailAsync("Some-Other-Folder", "gone@example.com");

        await _service.ReassignOrphanedEmailsAsync();

        var reloaded = await CreateContext(_databaseName).ReceivedEmails.SingleAsync(e => e.Id == email.Id);
        reloaded.Folder.ShouldBe("Some-Other-Folder");
    }

    [Test]
    [Ignore("EF Core ExecuteUpdateAsync requires a relational provider, not supported by the InMemory provider used here")]
    public async Task AssignInboxEmailsToClientsAsync_MovesKnownSendersOutOfInbox()
    {
        await AddCommunicationAsync("client@example.com");
        await AddReceivedEmailAsync(InboxImapName, "client@example.com");

        await _service.AssignInboxEmailsToClientsAsync();

        var reloaded = await _context.ReceivedEmails.SingleAsync();
        reloaded.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
    }

    [Test]
    public async Task GetStoredAddressAsync_ReturnsTheStoredSpellingOfTheClientsMatchingAddress()
    {
        var (client, _) = await AddClientWithCommunicationAsync("Anna.Muster@Example.com");

        var stored = await _service.GetStoredAddressAsync(client.Id, "anna.muster@example.com", CancellationToken.None);

        stored.ShouldBe("Anna.Muster@Example.com");
    }

    [Test]
    public async Task GetStoredAddressAsync_AddressOfAnotherClient_ReturnsNull()
    {
        await AddClientWithCommunicationAsync("anna@example.com");

        var stored = await _service.GetStoredAddressAsync(Guid.NewGuid(), "anna@example.com", CancellationToken.None);

        stored.ShouldBeNull();
    }

    [Test]
    public async Task GetStoredAddressAsync_DeletedCommunication_ReturnsNull()
    {
        var (client, _) = await AddClientWithCommunicationAsync("anna@example.com", communicationDeleted: true);

        var stored = await _service.GetStoredAddressAsync(client.Id, "anna@example.com", CancellationToken.None);

        stored.ShouldBeNull();
    }

    [Test]
    public async Task GetStoredAddressAsync_DeletedClient_ReturnsNull()
    {
        var (client, _) = await AddClientWithCommunicationAsync("anna@example.com", clientDeleted: true);

        var stored = await _service.GetStoredAddressAsync(client.Id, "anna@example.com", CancellationToken.None);

        stored.ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task GetStoredAddressAsync_BlankSenderAddress_ReturnsNull(string senderAddress)
    {
        var (client, _) = await AddClientWithCommunicationAsync("anna@example.com");

        var stored = await _service.GetStoredAddressAsync(client.Id, senderAddress, CancellationToken.None);

        stored.ShouldBeNull();
    }

    [Test]
    public async Task GetStoredAddressAsync_AddressSharedByTwoClients_ReturnsNull()
    {
        var (clientA, _) = await AddClientWithCommunicationAsync("shared@example.com");
        await AddClientWithCommunicationAsync("shared@example.com");

        var stored = await _service.GetStoredAddressAsync(clientA.Id, "shared@example.com", CancellationToken.None);

        stored.ShouldBeNull();
    }
}
