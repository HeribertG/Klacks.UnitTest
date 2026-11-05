// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Three-state bulk update of client availability: IsAvailable true/false upserts a slot, null removes the stored
/// row of the slot (back to "no entry"). Runs the handler against the real repository on an EF InMemory context;
/// InMemory does not enforce the unique (ClientId, Date, Hour) index, so the revive tests count physical rows.
/// </summary>

using Klacks.Api.Application.Commands.ClientAvailabilities;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Mappers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class BulkUpdateClientAvailabilityThreeStateTests
{
    private static readonly DateOnly Day = new(2026, 11, 9);
    private const int MorningHour = 8;
    private const int NoonHour = 12;
    private const int EveningHour = 18;

    private DataBaseContext _dbContext = null!;
    private ClientAvailabilityRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _clientId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ClientAvailabilityRepository(_dbContext, Substitute.For<ILogger<ClientAvailability>>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.CompleteAsync().Returns(_ => _dbContext.SaveChangesAsync());
        _clientId = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task Null_RemovesTheStoredRow()
    {
        await SeedAsync(MorningHour, true);

        await SendAsync(Item(MorningHour, null));

        (await ActiveRows()).ShouldBeEmpty();
        var stored = await AllPhysicalRows();
        stored.ShouldHaveSingleItem();
        stored[0].IsDeleted.ShouldBeTrue("the entity convention is a soft delete");
    }

    [Test]
    public async Task Null_ForMissingRow_IsNoOp()
    {
        await SeedAsync(NoonHour, false);

        var count = await SendAsync(Item(MorningHour, null));

        count.ShouldBe(1);
        var stored = await AllPhysicalRows();
        stored.ShouldHaveSingleItem();
        stored[0].Hour.ShouldBe(NoonHour);
        stored[0].IsDeleted.ShouldBeFalse();
        stored[0].IsAvailable.ShouldBeFalse();
    }

    [Test]
    public async Task MixedBatch_UpsertsTrueAndFalse_AndRemovesNull()
    {
        await SeedAsync(MorningHour, true);
        await SeedAsync(NoonHour, true);

        var count = await SendAsync(
            Item(MorningHour, null),
            Item(NoonHour, false),
            Item(EveningHour, true));

        count.ShouldBe(3);
        var active = (await ActiveRows()).OrderBy(r => r.Hour).ToList();
        active.Select(r => (r.Hour, r.IsAvailable)).ShouldBe(new[] { (NoonHour, false), (EveningHour, true) });
    }

    [Test]
    public async Task SetClearSet_RevivesTheSoftDeletedRow_InsteadOfInsertingASecondOne()
    {
        await SendAsync(Item(MorningHour, true));
        await SendAsync(Item(MorningHour, null));
        await SendAsync(Item(MorningHour, false));

        var stored = await AllPhysicalRows();
        stored.ShouldHaveSingleItem("the (ClientId, Date, Hour) index is unique over soft-deleted rows too");
        stored[0].IsDeleted.ShouldBeFalse();
        stored[0].DeletedTime.ShouldBeNull();
        stored[0].CurrentUserDeleted.ShouldBeNull();
        stored[0].IsAvailable.ShouldBeFalse();
    }

    [Test]
    public async Task SameSlotTwiceInOneBatch_LastItemWins()
    {
        await SeedAsync(MorningHour, false);

        var count = await SendAsync(Item(MorningHour, true), Item(MorningHour, null));

        count.ShouldBe(1);
        (await ActiveRows()).ShouldBeEmpty();
    }

    [Test]
    public async Task NullOnlyRequest_ForHiddenClient_IsRefusedLikeUnknownClient_NothingWritten()
    {
        var repository = Substitute.For<IClientAvailabilityRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var handler = new BulkUpdateCommandHandler(
            repository, TestGroupWriteVisibility.ClientsHidden(_clientId), unitOfWork,
            new ClientAvailabilityMapper(), Substitute.For<ILogger<BulkUpdateCommandHandler>>());
        var request = new ClientAvailabilityBulkRequest { Items = [Item(MorningHour, null)] };

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new BulkUpdateClientAvailabilityCommand(request), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_clientId} not found");
        await repository.DidNotReceive().BulkRemove(Arg.Any<IReadOnlyCollection<ClientAvailabilitySlot>>());
        await repository.DidNotReceive().BulkUpsert(Arg.Any<List<ClientAvailability>>());
        await unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task MixedRequest_PassesNullItemsAsSlotsToRemove_AndTheRestAsEntities()
    {
        var repository = Substitute.For<IClientAvailabilityRepository>();
        var handler = new BulkUpdateCommandHandler(
            repository, TestGroupWriteVisibility.AllClientsVisible(), _unitOfWork,
            new ClientAvailabilityMapper(), Substitute.For<ILogger<BulkUpdateCommandHandler>>());
        var request = new ClientAvailabilityBulkRequest
        {
            Items = [Item(MorningHour, null), Item(NoonHour, true)],
        };

        await handler.Handle(new BulkUpdateClientAvailabilityCommand(request), CancellationToken.None);

        await repository.Received(1).BulkRemove(Arg.Is<IReadOnlyCollection<ClientAvailabilitySlot>>(slots =>
            slots.Single() == new ClientAvailabilitySlot(_clientId, Day, MorningHour)));
        await repository.Received(1).BulkUpsert(Arg.Is<List<ClientAvailability>>(rows =>
            rows.Single().Hour == NoonHour && rows.Single().IsAvailable));
    }

    private async Task<int> SendAsync(params ClientAvailabilityResource[] items)
    {
        var handler = new BulkUpdateCommandHandler(
            _repository, TestGroupWriteVisibility.AllClientsVisible(), _unitOfWork,
            new ClientAvailabilityMapper(), Substitute.For<ILogger<BulkUpdateCommandHandler>>());
        var count = await handler.Handle(
            new BulkUpdateClientAvailabilityCommand(new ClientAvailabilityBulkRequest { Items = items.ToList() }),
            CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
        return count;
    }

    private async Task SeedAsync(int hour, bool isAvailable)
    {
        _dbContext.ClientAvailability.Add(new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            Date = Day,
            Hour = hour,
            IsAvailable = isAvailable,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private ClientAvailabilityResource Item(int hour, bool? isAvailable)
    {
        return new ClientAvailabilityResource { ClientId = _clientId, Date = Day, Hour = hour, IsAvailable = isAvailable };
    }

    private Task<List<ClientAvailability>> ActiveRows()
    {
        return _dbContext.ClientAvailability.AsNoTracking().ToListAsync();
    }

    private Task<List<ClientAvailability>> AllPhysicalRows()
    {
        return _dbContext.ClientAvailability.IgnoreQueryFilters().AsNoTracking().ToListAsync();
    }
}
