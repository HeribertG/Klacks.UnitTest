using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientRepositoryQualificationsTests
{
    private DataBaseContext _context = null!;
    private ClientRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _repository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            new ClientValidator(),
            Substitute.For<ILogger<ClientRepository>>());
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    [Test]
    public async Task Put_AddNewQualification_PersistsIt()
    {
        var client = await SeedClientWithQualificationAsync(QualificationLevel.Basic);
        var existingQualId = client.Qualifications.First().QualificationId;

        var updated = CopyClient(client);
        updated.Qualifications.Add(KeepExisting(client.Qualifications.First()));
        updated.Qualifications.Add(new ClientQualification
        {
            QualificationId = Guid.NewGuid(),
            Level = QualificationLevel.Expert
        });

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var saved = await ReloadAsync(client.Id);
        saved!.Qualifications.Count.ShouldBe(2);
        saved.Qualifications.ShouldContain(q => q.QualificationId == existingQualId);
        saved.Qualifications.ShouldContain(q => q.Level == QualificationLevel.Expert);
    }

    [Test]
    public async Task Put_ChangeQualificationLevel_UpdatesInPlace()
    {
        var client = await SeedClientWithQualificationAsync(QualificationLevel.Basic);

        var updated = CopyClient(client);
        var changed = KeepExisting(client.Qualifications.First());
        changed.Level = QualificationLevel.Advanced;
        updated.Qualifications.Add(changed);

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var saved = await ReloadAsync(client.Id);
        saved!.Qualifications.Count.ShouldBe(1);
        saved.Qualifications.First().Level.ShouldBe(QualificationLevel.Advanced);
    }

    [Test]
    public async Task Put_RemoveQualification_DeletesIt()
    {
        var client = await SeedClientWithQualificationAsync(QualificationLevel.Basic);

        var updated = CopyClient(client);
        // No qualifications carried over → the existing one must be removed.

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var saved = await ReloadAsync(client.Id);
        saved!.Qualifications.Count.ShouldBe(0);
    }

    [Test]
    public async Task Put_WithEmptyGroupItems_PreservesExistingGroups()
    {
        var client = await SeedClientWithGroupsAsync(Guid.NewGuid());
        var groupId = client.GroupItems.First().GroupId;

        var updated = CopyClient(client);
        // Partial save: no group items carried over → existing groups must be preserved.

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var activeGroups = await _context.GroupItem
            .Where(gi => gi.ClientId == client.Id && !gi.IsDeleted)
            .AsNoTracking()
            .ToListAsync();

        activeGroups.Count.ShouldBe(1);
        activeGroups.First().GroupId.ShouldBe(groupId);
    }

    [Test]
    public async Task Put_WithSameGroupId_KeepsRowWithoutRotation()
    {
        var client = await SeedClientWithGroupsAsync(Guid.NewGuid());
        var originalRowId = client.GroupItems.First().Id;
        var groupId = client.GroupItems.First().GroupId;

        var updated = CopyClient(client);
        updated.GroupItems.Add(new GroupItem { GroupId = groupId, ClientId = client.Id });

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var allRows = await _context.GroupItem
            .IgnoreQueryFilters()
            .Where(gi => gi.ClientId == client.Id)
            .AsNoTracking()
            .ToListAsync();

        allRows.Count.ShouldBe(1);
        allRows.Single().Id.ShouldBe(originalRowId);
        allRows.Single().IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task Put_RemoveOneOfTwoGroups_DeletesOnlyThatGroup()
    {
        var keptGroupId = Guid.NewGuid();
        var removedGroupId = Guid.NewGuid();
        var client = await SeedClientWithGroupsAsync(keptGroupId, removedGroupId);

        var updated = CopyClient(client);
        updated.GroupItems.Add(new GroupItem { GroupId = keptGroupId, ClientId = client.Id });

        await _repository.Put(updated);
        await _context.SaveChangesAsync();

        var activeGroups = await _context.GroupItem
            .Where(gi => gi.ClientId == client.Id && !gi.IsDeleted)
            .AsNoTracking()
            .ToListAsync();

        activeGroups.Count.ShouldBe(1);
        activeGroups.Single().GroupId.ShouldBe(keptGroupId);
    }

    private async Task<Client> SeedClientWithGroupsAsync(params Guid[] groupIds)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Tester",
            FirstName = "Group",
            Gender = GenderEnum.Female,
            IdNumber = 2
        };
        foreach (var groupId in groupIds)
        {
            // Seed the referenced Group too: ClientRepository.Put reloads group items via
            // .Include(c => c.GroupItems).ThenInclude(gi => gi.Group), which the InMemory
            // provider only materialises when the related Group row exists.
            _context.Set<Group>().Add(new Group { Id = groupId, Name = "G" });
            client.GroupItems.Add(new GroupItem
            {
                Id = Guid.NewGuid(),
                GroupId = groupId,
                ClientId = client.Id
            });
        }

        await _repository.Add(client);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return client;
    }

    private async Task<Client> SeedClientWithQualificationAsync(QualificationLevel level)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Tester",
            FirstName = "Quali",
            Gender = GenderEnum.Female,
            IdNumber = 1
        };
        client.Qualifications.Add(new ClientQualification
        {
            QualificationId = Guid.NewGuid(),
            ClientId = client.Id,
            Level = level
        });

        await _repository.Add(client);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return client;
    }

    private static Client CopyClient(Client source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        FirstName = source.FirstName,
        Gender = source.Gender,
        IdNumber = source.IdNumber
    };

    private static ClientQualification KeepExisting(ClientQualification source) => new()
    {
        Id = source.Id,
        ClientId = source.ClientId,
        QualificationId = source.QualificationId,
        Level = source.Level,
        ValidFrom = source.ValidFrom,
        ValidUntil = source.ValidUntil,
        Note = source.Note
    };

    private async Task<Client?> ReloadAsync(Guid id) => await _context.Client
        .Include(c => c.Qualifications)
        .AsNoTracking()
        .FirstOrDefaultAsync(c => c.Id == id);
}
