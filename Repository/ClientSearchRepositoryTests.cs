// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientSearchRepository.SearchAsync — verifies that an IdNumber included in the
/// search term narrows an ambiguous name match to exactly one client, which is what lets
/// search_and_navigate resolve a disambiguated selection deterministically instead of relying on
/// the LLM to carry the internal GUID id across a chat turn.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientSearchRepositoryTests
{
    private DataBaseContext _dbContext = null!;
    private ClientSearchRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _dbContext.Client.AddRange(
            new Client { Id = Guid.NewGuid(), FirstName = "Heribert", Name = "Gasparoli", IdNumber = 6556, Type = EntityTypeEnum.Employee },
            new Client { Id = Guid.NewGuid(), FirstName = "Heribert", Name = "Gasparoli", IdNumber = 7001, Type = EntityTypeEnum.Employee });
        _dbContext.SaveChanges();

        _repository = new ClientSearchRepository(_dbContext, Substitute.For<IClientGroupFilterService>());
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task SearchAsync_NameAlone_ReturnsBothAmbiguousMatches()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        result.TotalCount.ShouldBe(2);
    }

    [Test]
    public async Task SearchAsync_NamePlusIdNumber_NarrowsToExactSingleMatch()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli 6556", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(6556);
    }

    [Test]
    public async Task SearchAsync_IdNumberAlone_MatchesOnlyThatClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "7001", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(7001);
    }
}
