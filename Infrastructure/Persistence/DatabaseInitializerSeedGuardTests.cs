// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.StoredProcedures;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence;

[TestFixture]
public class DatabaseInitializerSeedGuardTests
{
    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task SeedDataAsync_UserExists_SkipsSeed()
    {
        _context.Users.Add(new IdentityUser { Id = Guid.NewGuid().ToString(), UserName = "admin" });
        await _context.SaveChangesAsync();
        var initializer = CreateInitializer(regionFilePath: null);

        await Should.NotThrowAsync(initializer.SeedDataAsync);
    }

    [Test]
    public async Task SeedDataAsync_ClientExists_SkipsSeed()
    {
        _context.Client.Add(new Client { Id = Guid.NewGuid(), FirstName = "John", Name = "Doe" });
        await _context.SaveChangesAsync();
        var initializer = CreateInitializer(regionFilePath: null);

        await Should.NotThrowAsync(initializer.SeedDataAsync);
    }

    [Test]
    public async Task SeedDataAsync_EmptyDatabaseAndRegionFileMissing_FailsFastBeforeSeeding()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        var initializer = CreateInitializer(missingPath);

        await Should.ThrowAsync<FileNotFoundException>(initializer.SeedDataAsync);
    }

    [Test]
    public async Task SeedDataAsync_EmptyDatabase_AttemptsSeeding()
    {
        var initializer = CreateInitializer(regionFilePath: null);

        await Should.ThrowAsync<Exception>(initializer.SeedDataAsync);
    }

    private DatabaseInitializer CreateInitializer(string? regionFilePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegionSetupFileReader.FileConfigKey] = regionFilePath
            })
            .Build();

        return new DatabaseInitializer(
            _context,
            NullLogger<DatabaseInitializer>.Instance,
            configuration,
            Substitute.For<IStoredProcedureInitializer>(),
            Substitute.For<IIdentityProviderSecretBackfill>());
    }
}
