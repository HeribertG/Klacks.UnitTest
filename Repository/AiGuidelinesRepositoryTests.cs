using Klacks.Api.Domain.Models.AI;
using Klacks.Api.Infrastructure.Repositories.AI;
using NUnit.Framework;
using FluentAssertions;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class AiGuidelinesRepositoryTests : BaseRepositoryTest
{
    private AiGuidelinesRepository _repository = null!;

    [SetUp]
    public override void BaseSetUp()
    {
        base.BaseSetUp();
        _repository = new AiGuidelinesRepository(TestDbContext);
    }

    [Test]
    public async Task GetActiveAsync_NoGuidelines_ReturnsNull()
    {
        var result = await _repository.GetActiveAsync();

        result.Should().BeNull();
    }

    [Test]
    public async Task GetActiveAsync_WithActiveGuidelines_ReturnsActive()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = "Test Guidelines",
            Content = "- Be helpful",
            IsActive = true,
            Source = "seed"
        };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetActiveAsync();

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Guidelines");
        result.Content.Should().Be("- Be helpful");
        result.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task GetActiveAsync_OnlyInactive_ReturnsNull()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = "Inactive",
            Content = "- Content",
            IsActive = false,
            Source = "seed"
        };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetActiveAsync();

        result.Should().BeNull();
    }

    [Test]
    public async Task GetActiveAsync_DeletedGuidelines_ReturnsNull()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = "Deleted",
            Content = "- Content",
            IsActive = true,
            IsDeleted = true,
            Source = "seed"
        };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetActiveAsync();

        result.Should().BeNull();
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllNonDeleted()
    {
        var g1 = new AiGuidelines { Id = Guid.NewGuid(), Name = "Active", Content = "A", IsActive = true, Source = "seed" };
        var g2 = new AiGuidelines { Id = Guid.NewGuid(), Name = "Inactive", Content = "B", IsActive = false, Source = "chat" };
        var g3 = new AiGuidelines { Id = Guid.NewGuid(), Name = "Deleted", Content = "C", IsActive = false, IsDeleted = true, Source = "seed" };
        await TestDbContext.AiGuidelines.AddRangeAsync(g1, g2, g3);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetAllAsync();

        result.Should().HaveCount(2);
        result.First().Name.Should().Be("Active");
    }

    [Test]
    public async Task GetByIdAsync_ExistingId_ReturnsGuidelines()
    {
        var id = Guid.NewGuid();
        var guidelines = new AiGuidelines { Id = id, Name = "Find Me", Content = "Content", IsActive = true, Source = "seed" };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Find Me");
    }

    [Test]
    public async Task GetByIdAsync_NonExistingId_ReturnsNull()
    {
        var result = await _repository.GetByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Test]
    public async Task GetByIdAsync_DeletedId_ReturnsNull()
    {
        var id = Guid.NewGuid();
        var guidelines = new AiGuidelines { Id = id, Name = "Deleted", Content = "C", IsActive = true, IsDeleted = true, Source = "seed" };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetByIdAsync(id);

        result.Should().BeNull();
    }

    [Test]
    public async Task AddAsync_AddsGuidelines()
    {
        var guidelines = new AiGuidelines
        {
            Id = Guid.NewGuid(),
            Name = "New",
            Content = "- New guidelines",
            IsActive = true,
            Source = "chat"
        };

        await _repository.AddAsync(guidelines);

        var result = await _repository.GetActiveAsync();
        result.Should().NotBeNull();
        result!.Name.Should().Be("New");
    }

    [Test]
    public async Task UpdateAsync_UpdatesGuidelines()
    {
        var id = Guid.NewGuid();
        var guidelines = new AiGuidelines { Id = id, Name = "Original", Content = "Old", IsActive = true, Source = "seed" };
        await TestDbContext.AiGuidelines.AddAsync(guidelines);
        await TestDbContext.SaveChangesAsync();

        var toUpdate = await _repository.GetByIdAsync(id);
        toUpdate!.Content = "Updated content";
        await _repository.UpdateAsync(toUpdate);

        var result = await _repository.GetByIdAsync(id);
        result!.Content.Should().Be("Updated content");
    }

    [Test]
    public async Task DeactivateAllAsync_DeactivatesAll()
    {
        var g1 = new AiGuidelines { Id = Guid.NewGuid(), Name = "G1", Content = "A", IsActive = true, Source = "seed" };
        var g2 = new AiGuidelines { Id = Guid.NewGuid(), Name = "G2", Content = "B", IsActive = true, Source = "chat" };
        await TestDbContext.AiGuidelines.AddRangeAsync(g1, g2);
        await TestDbContext.SaveChangesAsync();

        await _repository.DeactivateAllAsync();

        var active = await _repository.GetActiveAsync();
        active.Should().BeNull();

        var all = await _repository.GetAllAsync();
        all.Should().AllSatisfy(g => g.IsActive.Should().BeFalse());
    }

    [Test]
    public async Task DeactivateAllAsync_IgnoresDeleted()
    {
        var g1 = new AiGuidelines { Id = Guid.NewGuid(), Name = "Active", Content = "A", IsActive = true, Source = "seed" };
        var g2 = new AiGuidelines { Id = Guid.NewGuid(), Name = "Deleted", Content = "B", IsActive = true, IsDeleted = true, Source = "seed" };
        await TestDbContext.AiGuidelines.AddRangeAsync(g1, g2);
        await TestDbContext.SaveChangesAsync();

        await _repository.DeactivateAllAsync();

        var all = await _repository.GetAllAsync();
        all.Should().HaveCount(1);
        all.First().IsActive.Should().BeFalse();
    }
}
