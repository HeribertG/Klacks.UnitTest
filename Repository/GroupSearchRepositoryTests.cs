// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupSearchRepository.SearchAsync — verifies the substring fast path and the
/// fuzzy fallback: a misheard spoken name ("Mayer" for the group "Meier") resolves phonetically,
/// a label-decorated camel-case name resolves via the compact stage, and an unknown name stays
/// empty.
/// </summary>

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class GroupSearchRepositoryTests
{
    private static readonly Guid MeierId = Guid.NewGuid();
    private static readonly Guid NordOstId = Guid.NewGuid();

    private DataBaseContext _dbContext = null!;
    private GroupSearchRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _dbContext.Group.AddRange(
            new Group { Id = MeierId, Name = "Meier" },
            new Group { Id = NordOstId, Name = "NordOst" },
            new Group { Id = Guid.NewGuid(), Name = "Zürich" });
        _dbContext.SaveChanges();

        _repository = new GroupSearchRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task SearchAsync_SubstringHit_UsesFastPath()
    {
        var result = await _repository.SearchAsync("mei");

        result.Items.Count.ShouldBe(1);
        result.Items[0].Id.ShouldBe(MeierId);
    }

    [Test]
    public async Task SearchAsync_MisheardSpokenName_ResolvesPhonetically()
    {
        var result = await _repository.SearchAsync("Mayer");

        result.Items.Count.ShouldBe(1);
        result.Items[0].Id.ShouldBe(MeierId);
    }

    [Test]
    public async Task SearchAsync_LabelDecoratedCamelCaseName_ResolvesViaCompactStage()
    {
        var result = await _repository.SearchAsync("Gruppe Nord-Ost");

        result.Items.Count.ShouldBe(1);
        result.Items[0].Id.ShouldBe(NordOstId);
    }

    [Test]
    public async Task SearchAsync_UnknownName_StaysEmpty()
    {
        var result = await _repository.SearchAsync("Verwaltungsrat");

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }
}
