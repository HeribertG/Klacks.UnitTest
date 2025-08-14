using FluentAssertions;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace UnitTest.Services.Settings;

[TestFixture]
public class MacroTypeManagementServiceTests
{
    private DataBaseContext _context;
    private MacroTypeManagementService _service;
    private ILogger<MacroTypeManagementService> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<MacroTypeManagementService>>();
        _service = new MacroTypeManagementService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddMacroTypeAsync_WithValidMacroType_ShouldAddToContext()
    {
        var macroType = new MacroType
        {
            Id = Guid.NewGuid(),
            Name = "TestMacroType",
            IsDefault = true,
            Type = 1
        };

        await _service.AddMacroTypeAsync(macroType);
        await _context.SaveChangesAsync();

        var result = await _context.MacroType.FindAsync(macroType.Id);
        result.Should().NotBeNull();
        result.Name.Should().Be("TestMacroType");
        result.IsDefault.Should().BeTrue();
    }

    [Test]
    public async Task UpdateMacroTypeAsync_WithExistingMacroType_ShouldUpdateProperties()
    {
        var macroType = new MacroType
        {
            Id = Guid.NewGuid(),
            Name = "OriginalName",
            IsDefault = false,
            Type = 1
        };

        _context.MacroType.Add(macroType);
        await _context.SaveChangesAsync();

        macroType.Name = "UpdatedName";
        macroType.IsDefault = true;

        await _service.UpdateMacroTypeAsync(macroType);
        await _context.SaveChangesAsync();

        var result = await _context.MacroType.FindAsync(macroType.Id);
        result.Name.Should().Be("UpdatedName");
        result.IsDefault.Should().BeTrue();
    }

    [Test]
    public async Task DeleteMacroTypeAsync_WithExistingMacroType_ShouldRemoveFromContext()
    {
        var macroType = new MacroType
        {
            Id = Guid.NewGuid(),
            Name = "ToDelete",
            IsDefault = false,
            Type = 1
        };

        _context.MacroType.Add(macroType);
        await _context.SaveChangesAsync();

        await _service.DeleteMacroTypeAsync(macroType.Id);
        await _context.SaveChangesAsync();

        var result = await _context.MacroType.FindAsync(macroType.Id);
        result.Should().BeNull();
    }

    [Test]
    public async Task GetMacroTypeAsync_WithExistingId_ShouldReturnMacroType()
    {
        var macroType = new MacroType
        {
            Id = Guid.NewGuid(),
            Name = "TestMacroType",
            IsDefault = true,
            Type = 1
        };

        _context.MacroType.Add(macroType);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroTypeAsync(macroType.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(macroType.Id);
        result.Name.Should().Be("TestMacroType");
    }

    [Test]
    public async Task GetMacroTypeAsync_WithNonExistingId_ShouldThrowException()
    {
        var nonExistingId = Guid.NewGuid();

        await _service.Invoking(s => s.GetMacroTypeAsync(nonExistingId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetMacroTypeListAsync_WithMultipleMacroTypes_ShouldReturnAll()
    {
        var macroTypes = new[]
        {
            new MacroType { Id = Guid.NewGuid(), Name = "Type1", IsDefault = false, Type = 1 },
            new MacroType { Id = Guid.NewGuid(), Name = "Type2", IsDefault = true, Type = 2 },
            new MacroType { Id = Guid.NewGuid(), Name = "Type3", IsDefault = false, Type = 3 }
        };

        _context.MacroType.AddRange(macroTypes);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroTypeListAsync();

        result.Should().HaveCount(3);
        result.Should().Contain(mt => mt.Name == "Type1");
        result.Should().Contain(mt => mt.Name == "Type2");
        result.Should().Contain(mt => mt.Name == "Type3");
    }

    [Test]
    public async Task MacroTypeExistsAsync_WithExistingId_ShouldReturnTrue()
    {
        var macroType = new MacroType
        {
            Id = Guid.NewGuid(),
            Name = "TestMacroType",
            IsDefault = true,
            Type = 1
        };

        _context.MacroType.Add(macroType);
        await _context.SaveChangesAsync();

        var result = await _service.MacroTypeExistsAsync(macroType.Id);

        result.Should().BeTrue();
    }

    [Test]
    public async Task MacroTypeExistsAsync_WithNonExistingId_ShouldReturnFalse()
    {
        var nonExistingId = Guid.NewGuid();

        var result = await _service.MacroTypeExistsAsync(nonExistingId);

        result.Should().BeFalse();
    }
}