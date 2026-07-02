// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ShiftSearchRepository.SearchAsync — verifies that shifts are matched by their
/// own Abbreviation/Name, and that group-only shifts (no linked Client) are still searchable.
/// Regression coverage for three bugs: (1) search_and_navigate falsely reported an existing shift
/// as missing because the repository required a non-null Client and only matched on client name;
/// (2) a multi-word search term spanning both Name and Abbreviation (e.g. "Frühdienst FD", where
/// Name and Abbreviation live in separate columns) matched nothing because the whole term was
/// checked as one literal substring per field instead of tokenized; (3) OriginalOrder/SealedOrder
/// rows (order instances of a shift, sharing its name/abbreviation) surfaced as confusing duplicate
/// search results alongside the actual shift definition (OriginalShift/SplitShift).
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ShiftSearchRepositoryTests
{
    private DataBaseContext _dbContext = null!;
    private ShiftSearchRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        var client = new Client { Id = Guid.NewGuid(), FirstName = "Heribert", Name = "Gasparoli" };
        _dbContext.Client.Add(client);

        _dbContext.Shift.AddRange(
            new Shift { Id = Guid.NewGuid(), Abbreviation = "FS", Name = "Frühschicht", ClientId = null, Status = ShiftStatus.OriginalShift },
            new Shift { Id = Guid.NewGuid(), Abbreviation = "FD", Name = "Frühdienst", ClientId = null, Status = ShiftStatus.OriginalShift },
            new Shift { Id = Guid.NewGuid(), Abbreviation = "SS", Name = "Spätschicht", ClientId = client.Id, Client = client, Status = ShiftStatus.OriginalShift },
            new Shift { Id = Guid.NewGuid(), Abbreviation = "NS", Name = "Nachtschicht", ClientId = null, Status = ShiftStatus.SplitShift },
            new Shift { Id = Guid.NewGuid(), Abbreviation = "FS", Name = "Frühdienst", ClientId = null, Status = ShiftStatus.SealedOrder },
            new Shift { Id = Guid.NewGuid(), Abbreviation = "FS", Name = "Frühdienst", ClientId = null, Status = ShiftStatus.OriginalOrder });
        _dbContext.SaveChanges();

        _repository = new ShiftSearchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task SearchAsync_AbbreviationOfGroupOnlyShift_FindsShiftWithoutClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "FS", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Abbreviation.ShouldBe("FS");
    }

    [Test]
    public async Task SearchAsync_NameOfGroupOnlyShift_FindsShiftWithoutClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "Frühschicht", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Name.ShouldBe("Frühschicht");
    }

    [Test]
    public async Task SearchAsync_ClientName_StillMatchesShiftWithClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "Gasparoli", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().ClientLastName.ShouldBe("Gasparoli");
    }

    [Test]
    public async Task SearchAsync_NoTerm_ReturnsOnlyOriginalAndSplitShifts()
    {
        var result = await _repository.SearchAsync(limit: 10);

        result.TotalCount.ShouldBe(4);
    }

    [Test]
    public async Task SearchAsync_Name_ExcludesSealedAndOriginalOrderDuplicates()
    {
        var result = await _repository.SearchAsync(searchTerm: "Frühdienst", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Abbreviation.ShouldBe("FD");
    }

    [Test]
    public async Task SearchAsync_SplitShift_IsIncluded()
    {
        var result = await _repository.SearchAsync(searchTerm: "Nachtschicht", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Abbreviation.ShouldBe("NS");
    }

    [Test]
    public async Task SearchAsync_NamePlusAbbreviationTokens_MatchesSingleShiftAcrossBothColumns()
    {
        var result = await _repository.SearchAsync(searchTerm: "Frühdienst FD", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Name.ShouldBe("Frühdienst");
        result.Items.Single().Abbreviation.ShouldBe("FD");
    }

    [Test]
    public async Task SearchAsync_NamePlusWrongAbbreviationToken_MatchesNothing()
    {
        var result = await _repository.SearchAsync(searchTerm: "Frühdienst ZZ", limit: 10);

        result.TotalCount.ShouldBe(0);
    }

    [Test]
    public async Task SearchAsync_DisplayNameWithParentheses_MatchesShift()
    {
        var result = await _repository.SearchAsync(searchTerm: "Frühschicht (FS)", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Abbreviation.ShouldBe("FS");
    }
}
