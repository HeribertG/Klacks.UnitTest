using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class ClientAddressTypeValidatorTests
{
    private IClientRepository _clientRepository = null!;
    private IAddressRepository _addressRepository = null!;
    private ClientAddressTypeValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _addressRepository = Substitute.For<IAddressRepository>();
        _validator = new ClientAddressTypeValidator(_clientRepository, _addressRepository);
    }

    private static ClientResource ClientOf(EntityTypeEnum type, Guid? id, params AddressResource[] addresses) => new()
    {
        Id = id ?? Guid.Empty,
        Type = (int)type,
        Addresses = addresses.ToList(),
    };

    private static AddressResource AddressOf(AddressTypeEnum type, Guid? id = null) => new()
    {
        Id = id ?? Guid.Empty,
        Type = type,
    };

    private void StoredClientType(Guid clientId, EntityTypeEnum type) =>
        _clientRepository.GetTypeAndDisplayNameAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(type, "stored"));

    private void StoredAddressType(Guid addressId, AddressTypeEnum type) =>
        _addressRepository.GetNoTracking(addressId).Returns(new Address { Id = addressId, Type = type });

    [Test]
    public async Task NewClient_WithAllowedTypes_IsValid()
    {
        var client = ClientOf(EntityTypeEnum.ExternEmp, null, AddressOf(AddressTypeEnum.Employee), AddressOf(AddressTypeEnum.Workplace));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task NewEmployee_WithInvoicingAddress_IsRejected()
    {
        var client = ClientOf(EntityTypeEnum.Employee, null, AddressOf(AddressTypeEnum.InvoicingAddress));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Member(ClientAddressTypeRules.NotAllowedMessage));
    }

    [Test]
    public async Task StoredLegacyAddress_StaysAcceptable_WhileEntityTypeIsUnchanged()
    {
        var clientId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        StoredClientType(clientId, EntityTypeEnum.Employee);
        StoredAddressType(addressId, AddressTypeEnum.Workplace);
        var client = ClientOf(EntityTypeEnum.Employee, clientId, AddressOf(AddressTypeEnum.Workplace, addressId));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task NewDisallowedAddress_IsRejected_EvenWhenLegacyOnesExist()
    {
        var clientId = Guid.NewGuid();
        var legacyId = Guid.NewGuid();
        StoredClientType(clientId, EntityTypeEnum.Employee);
        StoredAddressType(legacyId, AddressTypeEnum.Workplace);
        var client = ClientOf(EntityTypeEnum.Employee, clientId,
            AddressOf(AddressTypeEnum.Workplace, legacyId),
            AddressOf(AddressTypeEnum.InvoicingAddress, Guid.NewGuid()));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public async Task RetypingStoredAddressToDisallowedType_IsRejected()
    {
        var clientId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        StoredClientType(clientId, EntityTypeEnum.Employee);
        StoredAddressType(addressId, AddressTypeEnum.Employee);
        var client = ClientOf(EntityTypeEnum.Employee, clientId, AddressOf(AddressTypeEnum.Workplace, addressId));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public async Task EntityTypeChange_WithAddressNotAllowedForNewType_IsRejected()
    {
        var clientId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        StoredClientType(clientId, EntityTypeEnum.Customer);
        StoredAddressType(addressId, AddressTypeEnum.InvoicingAddress);
        var client = ClientOf(EntityTypeEnum.Employee, clientId, AddressOf(AddressTypeEnum.InvoicingAddress, addressId));

        var result = await _validator.ValidateAsync(client);

        Assert.That(result.IsValid, Is.False);
    }
}
