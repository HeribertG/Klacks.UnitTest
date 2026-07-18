// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Scheduling;

[TestFixture]
public class SchedulingRuleRepositoryTests
{
    private DataBaseContext _context = null!;
    private SchedulingRuleRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new SchedulingRuleRepository(_context, Substitute.For<ILogger<SchedulingRule>>());

        _context.SchedulingRules.AddRange(
            new SchedulingRule { Id = Guid.NewGuid(), Name = "Manual rule", Industry = string.Empty },
            new SchedulingRule { Id = Guid.NewGuid(), Name = "Healthcare preset", Industry = "healthcare" },
            new SchedulingRule { Id = Guid.NewGuid(), Name = "Security preset", Industry = "Security" });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task GetSelectableAsync_SingleActiveIndustry_ReturnsIndustryLessAndMatchingRules()
    {
        var result = await _repository.GetSelectableAsync(new[] { "healthcare" });

        result.Select(r => r.Name).ShouldBe(new[] { "Manual rule", "Healthcare preset" }, ignoreOrder: true);
    }

    [Test]
    public async Task GetSelectableAsync_MixedCaseSlugAndRowValue_MatchesCaseInsensitively()
    {
        var result = await _repository.GetSelectableAsync(new[] { "SeCuRiTy" });

        result.Select(r => r.Name).ShouldBe(new[] { "Manual rule", "Security preset" }, ignoreOrder: true);
    }

    [Test]
    public async Task GetSelectableAsync_AllActiveIndustries_ReturnsEverything()
    {
        var result = await _repository.GetSelectableAsync(new[] { "healthcare", "security" });

        result.Count.ShouldBe(3);
    }
}
