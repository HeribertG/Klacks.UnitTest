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
public class VatManagementServiceTests
{
    private DataBaseContext _context;
    private VatManagementService _service;
    private ILogger<VatManagementService> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<VatManagementService>>();
        _service = new VatManagementService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task AddVatAsync_WithValidVat_ShouldAddToContext()
    {
        var vat = new Vat
        {
            Id = Guid.NewGuid(),
            VATRate = 19.0m,
            IsDefault = true
        };

        await _service.AddVatAsync(vat);
        await _context.SaveChangesAsync();

        var result = await _context.Vat.FindAsync(vat.Id);
        result.Should().NotBeNull();
        result.VATRate.Should().Be(19.0m);
        result.IsDefault.Should().BeTrue();
    }

    [Test]
    public async Task UpdateVatAsync_WithExistingVat_ShouldUpdateProperties()
    {
        var vat = new Vat
        {
            Id = Guid.NewGuid(),
            VATRate = 19.0m,
            IsDefault = false
        };

        _context.Vat.Add(vat);
        await _context.SaveChangesAsync();

        vat.VATRate = 16.0m;
        vat.IsDefault = true;

        await _service.UpdateVatAsync(vat);
        await _context.SaveChangesAsync();

        var result = await _context.Vat.FindAsync(vat.Id);
        result.VATRate.Should().Be(16.0m);
        result.IsDefault.Should().BeTrue();
    }

    [Test]
    public async Task DeleteVatAsync_WithExistingVat_ShouldRemoveFromContext()
    {
        var vat = new Vat
        {
            Id = Guid.NewGuid(),
            VATRate = 7.0m,
            IsDefault = false
        };

        _context.Vat.Add(vat);
        await _context.SaveChangesAsync();

        await _service.DeleteVatAsync(vat.Id);
        await _context.SaveChangesAsync();

        var result = await _context.Vat.FindAsync(vat.Id);
        result.Should().BeNull();
    }

    [Test]
    public async Task GetVatAsync_WithExistingId_ShouldReturnVat()
    {
        var vat = new Vat
        {
            Id = Guid.NewGuid(),
            VATRate = 19.0m,
            IsDefault = true
        };

        _context.Vat.Add(vat);
        await _context.SaveChangesAsync();

        var result = await _service.GetVatAsync(vat.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(vat.Id);
        result.VATRate.Should().Be(19.0m);
    }

    [Test]
    public async Task GetVatAsync_WithNonExistingId_ShouldThrowException()
    {
        var nonExistingId = Guid.NewGuid();

        await _service.Invoking(s => s.GetVatAsync(nonExistingId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task GetVatListAsync_WithMultipleVats_ShouldReturnAll()
    {
        var vats = new[]
        {
            new Vat { Id = Guid.NewGuid(), VATRate = 19.0m, IsDefault = true },
            new Vat { Id = Guid.NewGuid(), VATRate = 7.0m, IsDefault = false },
            new Vat { Id = Guid.NewGuid(), VATRate = 0.0m, IsDefault = false }
        };

        _context.Vat.AddRange(vats);
        await _context.SaveChangesAsync();

        var result = await _service.GetVatListAsync();

        result.Should().HaveCount(3);
        result.Should().Contain(v => v.VATRate == 19.0m);
        result.Should().Contain(v => v.VATRate == 7.0m);
        result.Should().Contain(v => v.VATRate == 0.0m);
    }

    [Test]
    public async Task VatExistsAsync_WithExistingId_ShouldReturnTrue()
    {
        var vat = new Vat
        {
            Id = Guid.NewGuid(),
            VATRate = 19.0m,
            IsDefault = true
        };

        _context.Vat.Add(vat);
        await _context.SaveChangesAsync();

        var result = await _service.VatExistsAsync(vat.Id);

        result.Should().BeTrue();
    }

    [Test]
    public async Task VatExistsAsync_WithNonExistingId_ShouldReturnFalse()
    {
        var nonExistingId = Guid.NewGuid();

        var result = await _service.VatExistsAsync(nonExistingId);

        result.Should().BeFalse();
    }
}