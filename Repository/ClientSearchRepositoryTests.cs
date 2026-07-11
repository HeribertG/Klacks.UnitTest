// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientSearchRepository.SearchAsync — verifies that an IdNumber included in the
/// search term narrows an ambiguous name match to exactly one client, which is what lets
/// search_and_navigate resolve a disambiguated selection deterministically instead of relying on
/// the LLM to carry the internal GUID id across a chat turn. Also covers the city, zip-prefix and
/// qualification-validity filters used by fill_group_by_criteria.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientSearchRepositoryTests
{
    private DataBaseContext _dbContext = null!;
    private ClientSearchRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _dbContext.Client.AddRange(
            new Client { Id = Guid.NewGuid(), FirstName = "Heribert", Name = "Gasparoli", IdNumber = 6556, Type = EntityTypeEnum.Employee },
            new Client { Id = Guid.NewGuid(), FirstName = "Heribert", Name = "Gasparoli", IdNumber = 7001, Type = EntityTypeEnum.Employee });
        _dbContext.SaveChanges();

        _repository = new ClientSearchRepository(_dbContext, Substitute.For<IClientGroupFilterService>());
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task SearchAsync_NameAlone_ReturnsBothAmbiguousMatches()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli", limit: 10);

        result.TotalCount.ShouldBe(2);
    }

    [Test]
    public async Task SearchAsync_NamePlusIdNumber_NarrowsToExactSingleMatch()
    {
        var result = await _repository.SearchAsync(searchTerm: "Heribert Gasparoli 6556", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(6556);
    }

    [Test]
    public async Task SearchAsync_IdNumberAlone_MatchesOnlyThatClient()
    {
        var result = await _repository.SearchAsync(searchTerm: "7001", limit: 10);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().IdNumber.ShouldBe(7001);
    }

    [Test]
    public async Task SearchAsync_CityFilter_MatchesExactly_CaseInsensitiveAndTrimmed()
    {
        var bernClientId = Guid.NewGuid();
        var zurichClientId = Guid.NewGuid();
        _dbContext.Client.AddRange(
            new Client { Id = bernClientId, FirstName = "A", Name = "One", Type = EntityTypeEnum.Employee },
            new Client { Id = zurichClientId, FirstName = "B", Name = "Two", Type = EntityTypeEnum.Employee });
        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = bernClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3000" },
            new Address { Id = Guid.NewGuid(), ClientId = zurichClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Zürich", Zip = "8000" });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: " bern ", zipPrefix: null, qualificationId: null, qualificationValidityDate: null,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(bernClientId);
    }

    [Test]
    public async Task SearchAsync_ZipPrefixFilter_MatchesStartsWith()
    {
        var matchingClientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        _dbContext.Client.AddRange(
            new Client { Id = matchingClientId, FirstName = "A", Name = "One", Type = EntityTypeEnum.Employee },
            new Client { Id = otherClientId, FirstName = "B", Name = "Two", Type = EntityTypeEnum.Employee });
        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = matchingClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = otherClientId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Basel", Zip = "4000" });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: null, zipPrefix: "30", qualificationId: null, qualificationValidityDate: null,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(matchingClientId);
    }

    [Test]
    public async Task SearchAsync_QualificationFilter_MatchesOnlyCurrentlyValidHolder()
    {
        var qualificationId = Guid.NewGuid();
        var referenceDate = new DateOnly(2026, 6, 1);
        var validHolderId = Guid.NewGuid();
        var expiredHolderId = Guid.NewGuid();
        var notYetValidHolderId = Guid.NewGuid();
        var nonHolderId = Guid.NewGuid();

        _dbContext.Client.AddRange(
            new Client { Id = validHolderId, FirstName = "A", Name = "Valid", Type = EntityTypeEnum.Employee },
            new Client { Id = expiredHolderId, FirstName = "B", Name = "Expired", Type = EntityTypeEnum.Employee },
            new Client { Id = notYetValidHolderId, FirstName = "C", Name = "Future", Type = EntityTypeEnum.Employee },
            new Client { Id = nonHolderId, FirstName = "D", Name = "None", Type = EntityTypeEnum.Employee });

        _dbContext.ClientQualification.AddRange(
            new ClientQualification { Id = Guid.NewGuid(), ClientId = validHolderId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = expiredHolderId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = new DateOnly(2026, 1, 1) },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = notYetValidHolderId, QualificationId = qualificationId, ValidFrom = new DateOnly(2027, 1, 1), ValidUntil = null });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: null, zipPrefix: null, qualificationId: qualificationId, qualificationValidityDate: referenceDate,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(validHolderId);
    }

    [Test]
    public async Task SearchAsync_CombinesCityZipPrefixAndQualification_WithAndSemantics()
    {
        var qualificationId = Guid.NewGuid();
        var referenceDate = new DateOnly(2026, 6, 1);
        var fullMatchId = Guid.NewGuid();
        var wrongCityId = Guid.NewGuid();
        var noQualificationId = Guid.NewGuid();

        _dbContext.Client.AddRange(
            new Client { Id = fullMatchId, FirstName = "A", Name = "Full", Type = EntityTypeEnum.Employee },
            new Client { Id = wrongCityId, FirstName = "B", Name = "WrongCity", Type = EntityTypeEnum.Employee },
            new Client { Id = noQualificationId, FirstName = "C", Name = "NoQualification", Type = EntityTypeEnum.Employee });

        _dbContext.Address.AddRange(
            new Address { Id = Guid.NewGuid(), ClientId = fullMatchId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = wrongCityId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Basel", Zip = "3007" },
            new Address { Id = Guid.NewGuid(), ClientId = noQualificationId, Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), City = "Bern", Zip = "3007" });

        _dbContext.ClientQualification.AddRange(
            new ClientQualification { Id = Guid.NewGuid(), ClientId = fullMatchId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null },
            new ClientQualification { Id = Guid.NewGuid(), ClientId = wrongCityId, QualificationId = qualificationId, ValidFrom = null, ValidUntil = null });
        _dbContext.SaveChanges();

        var result = await _repository.SearchAsync(
            searchTerm: null, canton: null, entityType: null, contractId: null,
            city: "Bern", zipPrefix: "30", qualificationId: qualificationId, qualificationValidityDate: referenceDate,
            limit: 10, cancellationToken: CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Items.Single().Id.ShouldBe(fullMatchId);
    }
}
