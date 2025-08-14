using FluentAssertions;
using Klacks.Api.Domain.Common;
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
public class MacroManagementServiceTests
{
    private DataBaseContext _context;
    private MacroManagementService _service;
    private ILogger<MacroManagementService> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<MacroManagementService>>();
        _service = new MacroManagementService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddMacroAsync_WithValidMacro_ShouldAddToContext()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        await _service.AddMacroAsync(macro);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.FindAsync(macro.Id);
        result.Should().NotBeNull();
        result.Name.Should().Be("TestMacro");
        result.Content.Should().Be("TestContent");
    }

    [Test]
    public async Task UpdateMacroAsync_WithExistingMacro_ShouldUpdateProperties()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "OriginalName",
            Content = "OriginalContent",
            Description = new MultiLanguage { De = "Original Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        macro.Name = "UpdatedName";
        macro.Content = "UpdatedContent";

        await _service.UpdateMacroAsync(macro);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.FindAsync(macro.Id);
        result.Name.Should().Be("UpdatedName");
        result.Content.Should().Be("UpdatedContent");
    }

    [Test]
    public async Task DeleteMacroAsync_WithExistingMacro_ShouldRemoveFromContext()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "ToDelete",
            Content = "DeleteContent",
            Description = new MultiLanguage { De = "Delete Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        await _service.DeleteMacroAsync(macro.Id);
        await _context.SaveChangesAsync();

        var result = await _context.Macro.FindAsync(macro.Id);
        result.Should().BeNull();
    }

    [Test]
    public async Task GetMacroAsync_WithExistingId_ShouldReturnMacro()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroAsync(macro.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(macro.Id);
        result.Name.Should().Be("TestMacro");
    }

    [Test]
    public async Task GetMacroAsync_WithNonExistingId_ShouldThrowException()
    {
        var nonExistingId = Guid.NewGuid();

        await _service.Invoking(s => s.GetMacroAsync(nonExistingId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetMacroListAsync_WithMultipleMacros_ShouldReturnAll()
    {
        var macros = new[]
        {
            new Macro { Id = Guid.NewGuid(), Name = "Macro1", Content = "Content1", Description = new MultiLanguage { De = "Desc1" }, Type = 1 },
            new Macro { Id = Guid.NewGuid(), Name = "Macro2", Content = "Content2", Description = new MultiLanguage { De = "Desc2" }, Type = 2 },
            new Macro { Id = Guid.NewGuid(), Name = "Macro3", Content = "Content3", Description = new MultiLanguage { De = "Desc3" }, Type = 3 }
        };

        _context.Macro.AddRange(macros);
        await _context.SaveChangesAsync();

        var result = await _service.GetMacroListAsync();

        result.Should().HaveCount(3);
        result.Should().Contain(m => m.Name == "Macro1");
        result.Should().Contain(m => m.Name == "Macro2");
        result.Should().Contain(m => m.Name == "Macro3");
    }

    [Test]
    public async Task MacroExistsAsync_WithExistingId_ShouldReturnTrue()
    {
        var macro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "TestMacro",
            Content = "TestContent",
            Description = new MultiLanguage { De = "Test Description" },
            Type = 1
        };

        _context.Macro.Add(macro);
        await _context.SaveChangesAsync();

        var result = await _service.MacroExistsAsync(macro.Id);

        result.Should().BeTrue();
    }

    [Test]
    public async Task MacroExistsAsync_WithNonExistingId_ShouldReturnFalse()
    {
        var nonExistingId = Guid.NewGuid();

        var result = await _service.MacroExistsAsync(nonExistingId);

        result.Should().BeFalse();
    }
}