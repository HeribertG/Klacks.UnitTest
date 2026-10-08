// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ExportLogItemRepository against an in-memory EF Core database (which does not enforce indexes, so the unique revision guard is only asserted on the model and left to the integration test for the 23505 behaviour): writes are only staged, the
/// latest export is returned per person for exactly one period and format, and the overlap query returns only
/// items of the asked persons whose period overlaps without having the same bounds.
/// </summary>
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Exports;

[TestFixture]
public class ExportLogItemRepositoryTests
{
    private const string FormatDatev = "datev-lug-bewegungsdaten";
    private const string FormatPaxml = "paxml-se";

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly Until = new(2026, 1, 31);

    private DataBaseContext _context = null!;
    private ExportLogItemRepository _repository = null!;

    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();
    private readonly Guid _clientC = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new ExportLogItemRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddRangeAsync_OnlyStagesTheItems_AndAssignsIds()
    {
        var item = NewItem(_clientA, From, Until, FormatDatev, "hash-1");
        item.Id = Guid.Empty;

        await _repository.AddRangeAsync([item]);

        item.Id.ShouldNotBe(Guid.Empty);
        _context.ChangeTracker.Entries<ExportLogItem>().Count(e => e.State == EntityState.Added).ShouldBe(1);
        _context.ExportLogItem.AsNoTracking().Count().ShouldBe(0);

        await _context.SaveChangesAsync();
        _context.ExportLogItem.AsNoTracking().Count().ShouldBe(1);
    }

    [Test]
    public async Task GetLatestItemsAsync_ReturnsTheHighestRevisionPerPerson_ForExactPeriodAndFormatOnly()
    {
        await Seed(
            NewItem(_clientA, From, Until, FormatDatev, "a-1", revision: 1),
            NewItem(_clientA, From, Until, FormatDatev, "a-2", revision: 2),
            NewItem(_clientB, From, Until, FormatDatev, "b-1", revision: 1),
            NewItem(_clientA, From, Until, FormatPaxml, "a-other-format", revision: 5),
            NewItem(_clientA, From, Until.AddDays(-1), FormatDatev, "a-other-bounds", revision: 9),
            NewItem(_clientC, From, Until, FormatDatev, "c-deleted", revision: 1, isDeleted: true));

        var latest = await _repository.GetLatestItemsAsync(From, Until, FormatDatev, null);

        latest.Keys.ShouldBe([_clientA, _clientB], ignoreOrder: true);
        latest[_clientA].ContentHash.ShouldBe("a-2");
        latest[_clientA].Revision.ShouldBe(2);
        latest[_clientB].ContentHash.ShouldBe("b-1");
        latest[_clientB].Revision.ShouldBe(1);
    }

    [Test]
    public async Task GetLatestItemsAsync_AfterReturnToEarlierContent_LatestIsTheNewestRevision_NotSetMembership()
    {
        await Seed(
            NewItem(_clientA, From, Until, FormatDatev, "content-A", revision: 1),
            NewItem(_clientA, From, Until, FormatDatev, "content-B", revision: 2));

        var latest = await _repository.GetLatestItemsAsync(From, Until, FormatDatev, null);

        var latestForA = latest[_clientA];
        latestForA.ContentHash.ShouldBe("content-B");
        latestForA.ContentHash.ShouldNotBe("content-A");

        await Seed(NewItem(_clientA, From, Until, FormatDatev, "content-A", revision: latestForA.Revision + 1));

        var afterReturn = await _repository.GetLatestItemsAsync(From, Until, FormatDatev, null);

        afterReturn[_clientA].ContentHash.ShouldBe("content-A");
        afterReturn[_clientA].Revision.ShouldBe(3);
    }

    [Test]
    public async Task GetLatestItemsAsync_RestrictsToTheGivenPersons()
    {
        await Seed(
            NewItem(_clientA, From, Until, FormatDatev, "a-1"),
            NewItem(_clientB, From, Until, FormatDatev, "b-1"));

        var latest = await _repository.GetLatestItemsAsync(From, Until, FormatDatev, [_clientB]);

        latest.Keys.ShouldBe([_clientB]);
    }

    [Test]
    public async Task GetLatestItemsAsync_WithoutItems_ReturnsEmpty()
    {
        var latest = await _repository.GetLatestItemsAsync(From, Until, FormatDatev, null);

        latest.ShouldBeEmpty();
    }
    [Test]
    public async Task GetOverlappingAsync_ReturnsOverlappingItemsWithDifferentBounds_OfTheAskedPersonsOnly()
    {
        var sameBounds = NewItem(_clientA, From, Until, FormatDatev, "same");
        var shorter = NewItem(_clientA, From, new DateOnly(2026, 1, 15), FormatDatev, "shorter");
        var shifted = NewItem(_clientA, new DateOnly(2026, 1, 20), new DateOnly(2026, 2, 20), FormatPaxml, "shifted");
        var enclosing = NewItem(_clientB, new DateOnly(2025, 12, 1), new DateOnly(2026, 3, 31), FormatDatev, "enclosing");
        var adjacentBefore = NewItem(_clientA, new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), FormatDatev, "before");
        var adjacentAfter = NewItem(_clientA, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), FormatDatev, "after");
        var foreignPerson = NewItem(_clientC, From, new DateOnly(2026, 1, 15), FormatDatev, "foreign");
        var deleted = NewItem(_clientA, From, new DateOnly(2026, 1, 10), FormatDatev, "deleted", isDeleted: true);
        await Seed(sameBounds, shorter, shifted, enclosing, adjacentBefore, adjacentAfter, foreignPerson, deleted);

        var overlapping = await _repository.GetOverlappingAsync([_clientA, _clientB], From, Until);

        overlapping.Select(i => i.ContentHash).ShouldBe(["shorter", "shifted", "enclosing"], ignoreOrder: true);
    }

    [Test]
    public async Task GetOverlappingAsync_WithoutOverlap_ReturnsEmpty()
    {
        await Seed(NewItem(_clientA, From, Until, FormatDatev, "same"));

        var overlapping = await _repository.GetOverlappingAsync([_clientA], From, Until);

        overlapping.ShouldBeEmpty();
    }

    [Test]
    public void TheModel_HasAUniqueRevisionIndexPerPersonPeriodAndFormat_ThatGuardsConcurrentExports()
    {
        var index = _context.Model.FindEntityType(typeof(ExportLogItem))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        index.Properties.Select(property => property.Name).ShouldBe(
            [
                nameof(ExportLogItem.ClientId),
                nameof(ExportLogItem.StartDate),
                nameof(ExportLogItem.EndDate),
                nameof(ExportLogItem.Format),
                nameof(ExportLogItem.Revision),
            ]);
        index.GetFilter().ShouldNotBeNull().ShouldContain("is_deleted");
    }

    private async Task Seed(params ExportLogItem[] items)
    {
        await _repository.AddRangeAsync(items);
        await _context.SaveChangesAsync();
    }

    private static ExportLogItem NewItem(
        Guid clientId,
        DateOnly start,
        DateOnly end,
        string format,
        string hash,
        int revision = 1,
        bool isDeleted = false)
    {
        return new ExportLogItem
        {
            Id = Guid.NewGuid(),
            ExportLogId = Guid.NewGuid(),
            ClientId = clientId,
            StartDate = start,
            EndDate = end,
            Format = format,
            Revision = revision,
            ContentHash = hash,
            EntryCount = 1,
            EntriesJson = "[]",
            IsDeleted = isDeleted,
        };
    }
}
