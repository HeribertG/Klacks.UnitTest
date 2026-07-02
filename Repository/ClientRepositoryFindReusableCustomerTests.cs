using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ClientRepositoryFindReusableCustomerTests
{
    private DataBaseContext _context = null!;
    private IClientRepository _clientRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockLogger = Substitute.For<ILogger<ClientRepository>>();

        _clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            collectionUpdateService,
            Substitute.For<IClientValidator>(),
            mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task FindReusableCustomerAsync_MatchesByExternalReference_EvenWhenAddressDiffers()
    {
        var existing = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "Alten- und Pflegeheim Sonnhalde",
            SourceSystemId = "erp-1",
            ExternalCustomerReference = "CUST-4711",
            Addresses = [new Address { Zip = "3000", Street = "Bernstrasse 1", ValidFrom = DateTime.UtcNow }]
        };
        _context.Client.Add(existing);
        await _context.SaveChangesAsync();

        var candidate = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "Alten- und Pflegeheim Sonnhalde AG",
            SourceSystemId = "erp-1",
            ExternalCustomerReference = "CUST-4711",
            Addresses = [new Address { Zip = "3001", Street = "Andere Strasse 9", ValidFrom = DateTime.UtcNow }]
        };

        var result = await _clientRepository.FindReusableCustomerAsync(candidate);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(existing.Id);
    }

    [Test]
    public async Task FindReusableCustomerAsync_FallsBackToBusinessKey_WhenExternalReferenceUnknown()
    {
        var existing = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "Spitex Musterhausen",
            Addresses = [new Address { Zip = "4000", Street = "Hauptstrasse 5", ValidFrom = DateTime.UtcNow }]
        };
        _context.Client.Add(existing);
        await _context.SaveChangesAsync();

        var candidate = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "spitex musterhausen",
            SourceSystemId = "erp-1",
            ExternalCustomerReference = "CUST-9999",
            Addresses = [new Address { Zip = "4000", Street = "hauptstrasse 5", ValidFrom = DateTime.UtcNow }]
        };

        var result = await _clientRepository.FindReusableCustomerAsync(candidate);

        result.ShouldNotBeNull();
        result!.Id.ShouldBe(existing.Id);
    }

    [Test]
    public async Task FindReusableCustomerAsync_ReturnsNull_WhenNeitherReferenceNorBusinessKeyMatch()
    {
        var existing = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "Spitex Musterhausen",
            SourceSystemId = "erp-1",
            ExternalCustomerReference = "CUST-4711",
            Addresses = [new Address { Zip = "4000", Street = "Hauptstrasse 5", ValidFrom = DateTime.UtcNow }]
        };
        _context.Client.Add(existing);
        await _context.SaveChangesAsync();

        var candidate = new Client
        {
            Type = EntityTypeEnum.Customer,
            Company = "Voellig andere Firma",
            SourceSystemId = "erp-1",
            ExternalCustomerReference = "CUST-0000",
            Addresses = [new Address { Zip = "9000", Street = "Fremde Gasse 2", ValidFrom = DateTime.UtcNow }]
        };

        var result = await _clientRepository.FindReusableCustomerAsync(candidate);

        result.ShouldBeNull();
    }
}
