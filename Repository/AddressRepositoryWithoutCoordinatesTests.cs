// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests AddressRepository.GetWithoutCoordinatesAsync: it returns exactly the live addresses with a city whose
/// latitude or longitude is missing - the selection the batch geocoding used to run inline in the controller.
/// </summary>

using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class AddressRepositoryWithoutCoordinatesTests
{
    private DataBaseContext _context = null!;
    private AddressRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new AddressRepository(_context, NullLogger<Address>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetWithoutCoordinatesAsync_ReturnsOnlyLiveAddressesWithCityAndMissingCoordinates()
    {
        // Arrange
        var noCoordinates = NewAddress("Zürich", null, null);
        var latitudeOnly = NewAddress("Bern", 46.9, null);
        var geocoded = NewAddress("Basel", 47.5, 7.6);
        var withoutCity = NewAddress(string.Empty, null, null);
        var deleted = NewAddress("Luzern", null, null);
        deleted.IsDeleted = true;
        _context.Address.AddRange(noCoordinates, latitudeOnly, geocoded, withoutCity, deleted);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetWithoutCoordinatesAsync();

        // Assert
        result.Select(a => a.Id).ShouldBe([noCoordinates.Id, latitudeOnly.Id], ignoreOrder: true);
        var first = result.Single(a => a.Id == noCoordinates.Id);
        first.Street.ShouldBe(noCoordinates.Street);
        first.Zip.ShouldBe(noCoordinates.Zip);
        first.City.ShouldBe("Zürich");
        first.Country.ShouldBe("CH");
    }

    private static Address NewAddress(string city, double? latitude, double? longitude) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Street = "Bahnhofstrasse 1",
        Zip = "8001",
        City = city,
        Country = "CH",
        Latitude = latitude,
        Longitude = longitude,
    };
}
