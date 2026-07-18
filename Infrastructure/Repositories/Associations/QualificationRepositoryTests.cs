// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Associations;

[TestFixture]
public class QualificationRepositoryTests
{
    private DataBaseContext _context = null!;
    private QualificationRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new QualificationRepository(_context, Substitute.For<ILogger<Qualification>>());

        _context.Qualification.AddRange(
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Zusatzausbildung" }, Industry = string.Empty },
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Anästhesiepflege" }, Industry = "healthcare" },
            new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Bewachung" }, Industry = "Security" });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task GetSelectableAsync_SingleActiveIndustry_ReturnsIndustryLessAndMatchingRowsOrderedByName()
    {
        var result = await _repository.GetSelectableAsync(new[] { "healthcare" });

        result.Select(q => q.Name.De).ShouldBe(new[] { "Anästhesiepflege", "Zusatzausbildung" });
    }

    [Test]
    public async Task GetSelectableAsync_MixedCaseSlugAndRowValue_MatchesCaseInsensitively()
    {
        var result = await _repository.GetSelectableAsync(new[] { "SECURITY" });

        result.Select(q => q.Name.De).ShouldBe(new[] { "Bewachung", "Zusatzausbildung" });
    }

    [Test]
    public async Task GetAllAsync_ReturnsEverything()
    {
        var result = await _repository.GetAllAsync();

        result.Count.ShouldBe(3);
    }
}
