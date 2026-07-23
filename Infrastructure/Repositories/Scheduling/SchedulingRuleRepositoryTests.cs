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
            new SchedulingRule { Id = Guid.NewGuid(), Name = "Security preset", Industry = "security" });
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
    public async Task GetSelectableAsync_MixedCaseSlug_NormalizesToLowercaseAndMatchesPersistedRow()
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

    [Test]
    public async Task GetSelectableAsync_EmptySlugCollection_ReturnsOnlyIndustryLessRules()
    {
        var result = await _repository.GetSelectableAsync(Array.Empty<string>());

        result.Select(r => r.Name).ShouldBe(new[] { "Manual rule" });
    }

    [Test]
    public async Task GetByIndustryAsync_MixedCaseArgument_NormalizesToLowercaseAndMatchesPersistedRow()
    {
        var result = await _repository.GetByIndustryAsync("SECURITY");

        result.Select(r => r.Name).ShouldBe(new[] { "Security preset" });
    }

    [Test]
    public async Task GetByIndustryAsync_NoRuleForIndustry_ReturnsEmpty()
    {
        var result = await _repository.GetByIndustryAsync("logistics");

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetCustomRuleCountAsync_MixOfCustomAndIndustryBoundRules_CountsOnlyCustomRules()
    {
        var result = await _repository.GetCustomRuleCountAsync();

        result.ShouldBe(1);
    }

    [Test]
    public async Task GetCustomRuleCountAsync_NoCustomRules_ReturnsZero()
    {
        var industryOnlyContext = new DataBaseContext(
            new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            Substitute.For<IHttpContextAccessor>());
        industryOnlyContext.SchedulingRules.Add(
            new SchedulingRule { Id = Guid.NewGuid(), Name = "Healthcare preset", Industry = "healthcare" });
        industryOnlyContext.SaveChanges();
        var repository = new SchedulingRuleRepository(industryOnlyContext, Substitute.For<ILogger<SchedulingRule>>());

        var result = await repository.GetCustomRuleCountAsync();

        result.ShouldBe(0);
        industryOnlyContext.Dispose();
    }

    [Test]
    public async Task GetCustomRuleCountAsync_SoftDeletedCustomRule_IsExcluded()
    {
        var customRule = new SchedulingRule { Id = Guid.NewGuid(), Name = "Second manual rule", Industry = string.Empty };
        _context.SchedulingRules.Add(customRule);
        _context.SaveChanges();
        customRule.IsDeleted = true;
        _context.SaveChanges();

        var result = await _repository.GetCustomRuleCountAsync();

        result.ShouldBe(1);
    }
}
