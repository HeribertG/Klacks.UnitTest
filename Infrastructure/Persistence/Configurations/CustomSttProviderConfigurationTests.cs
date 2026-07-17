// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that CustomSttProvider.ApiKey is encrypted at rest via EncryptedStringConverter
/// when a real ISettingsEncryptionService is available, falls back to plain storage when it is
/// not (test/no-DI scenario), and that the column length was raised to fit encrypted payloads.
/// </summary>

using System;
using System.Threading.Tasks;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Persistence.Configurations;
using Klacks.Api.Infrastructure.Persistence.Converters;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Configurations;

[TestFixture]
public class CustomSttProviderConfigurationTests
{
    private ISettingsEncryptionService _encryptionService = null!;

    // Two distinct DbContext types (rather than one type branching on a constructor
    // argument) because EF Core's default model cache key is scoped to the context
    // type, not to constructor state - reusing one type for both the encrypted and
    // unencrypted variant would let the first-built model leak into the other test.
    private sealed class EncryptedCustomSttContext : DbContext
    {
        private readonly ISettingsEncryptionService _encryptionService;

        public EncryptedCustomSttContext(DbContextOptions<EncryptedCustomSttContext> options, ISettingsEncryptionService encryptionService)
            : base(options)
        {
            _encryptionService = encryptionService;
        }

        public DbSet<CustomSttProvider> CustomSttProviders => Set<CustomSttProvider>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new CustomSttProviderConfiguration(_encryptionService));
        }
    }

    private sealed class PlainCustomSttContext : DbContext
    {
        public PlainCustomSttContext(DbContextOptions<PlainCustomSttContext> options)
            : base(options)
        {
        }

        public DbSet<CustomSttProvider> CustomSttProviders => Set<CustomSttProvider>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new CustomSttProviderConfiguration(null));
        }
    }

    [SetUp]
    public void Setup()
    {
        _encryptionService = new SettingsEncryptionService(
            new EphemeralDataProtectionProvider(),
            NullLogger<SettingsEncryptionService>.Instance);
    }

    [Test]
    public void Configure_WithEncryptionService_AppliesEncryptedStringConverterAndRaisedMaxLength()
    {
        var options = new DbContextOptionsBuilder<EncryptedCustomSttContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new EncryptedCustomSttContext(options, _encryptionService);

        var apiKeyProperty = context.Model.FindEntityType(typeof(CustomSttProvider))!
            .FindProperty(nameof(CustomSttProvider.ApiKey))!;

        apiKeyProperty.GetValueConverter().ShouldNotBeNull();
        apiKeyProperty.GetMaxLength().ShouldBe(2000);
    }

    [Test]
    public void Configure_WithoutEncryptionService_LeavesApiKeyUnconverted()
    {
        var options = new DbContextOptionsBuilder<PlainCustomSttContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new PlainCustomSttContext(options);

        var apiKeyProperty = context.Model.FindEntityType(typeof(CustomSttProvider))!
            .FindProperty(nameof(CustomSttProvider.ApiKey))!;

        apiKeyProperty.GetValueConverter().ShouldBeNull();
    }

    [Test]
    public void EncryptedStringConverter_GivenPlainApiKey_ProducesDistinctEncPrefixedValueThatDecryptsBack()
    {
        const string plainApiKey = "sk-test-0123456789abcdef0123456789abcdef";
        var converter = new EncryptedStringConverter(_encryptionService);
        var toStore = converter.ConvertToProviderExpression.Compile();
        var fromStore = converter.ConvertFromProviderExpression.Compile();

        var stored = toStore(plainApiKey);

        stored.ShouldNotBeNull();
        stored.ShouldStartWith("ENC:");
        stored.ShouldNotBe(plainApiKey);

        var roundTripped = fromStore(stored);
        roundTripped.ShouldBe(plainApiKey);
    }

    [Test]
    public void EncryptedStringConverter_GivenLegacyPlainTextFromStore_ReturnsItUnchanged()
    {
        const string legacyPlainApiKey = "legacy-unencrypted-key";
        var converter = new EncryptedStringConverter(_encryptionService);
        var fromStore = converter.ConvertFromProviderExpression.Compile();

        fromStore(legacyPlainApiKey).ShouldBe(legacyPlainApiKey);
    }

    [Test]
    public async Task SaveChanges_NewProviderWithApiKey_ReadsBackExactPlaintextThroughEncryptedColumn()
    {
        const string plainApiKey = "sk-round-trip-abcdefghijklmnopqrstuvwxyz";

        // A dedicated internal service provider isolates this test's compiled model from EF's
        // default per-context-type model cache, which every other DataBaseContext-constructing
        // test in this assembly shares. Without this, whichever test builds the DataBaseContext
        // model first (with or without an encryption service) wins for the rest of the run,
        // making this test's outcome depend on assembly-wide execution order.
        var isolatedServiceProvider = new ServiceCollection()
            .AddEntityFrameworkInMemoryDatabase()
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseInternalServiceProvider(isolatedServiceProvider)
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var providerId = Guid.NewGuid();

        await using (var writeContext = new DataBaseContext(options, httpContextAccessor, _encryptionService))
        {
            // Guards against EF's per-context-type model cache silently reusing a model built
            // by another test with a null encryption service, which would make this assertion
            // pass trivially without ever exercising the encrypted column path.
            writeContext.Model.FindEntityType(typeof(CustomSttProvider))!
                .FindProperty(nameof(CustomSttProvider.ApiKey))!
                .GetValueConverter().ShouldNotBeNull();

            writeContext.CustomSttProviders.Add(new CustomSttProvider
            {
                Id = providerId,
                Name = "Encryption Round-Trip Test Provider",
                ConnectionType = "rest",
                ApiUrl = "https://stt.example.test/v1",
                ApiKey = plainApiKey,
                IsEnabled = true,
            });
            await writeContext.SaveChangesAsync();
        }

        await using var readContext = new DataBaseContext(options, httpContextAccessor, _encryptionService);
        var reloaded = await readContext.CustomSttProviders.AsNoTracking().FirstAsync(p => p.Id == providerId);

        reloaded.ApiKey.ShouldBe(plainApiKey);
    }
}
