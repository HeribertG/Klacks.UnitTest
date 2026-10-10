using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;

using AddressPostValidator = Klacks.Api.Application.Validation.Addresses.PostCommandValidator;
using AddressPutValidator = Klacks.Api.Application.Validation.Addresses.PutCommandValidator;

namespace Klacks.UnitTest.Validation.Addresses;

[TestFixture]
public class AddressTypeCommandValidatorTests
{
    private IClientRepository _clientRepository = null!;
    private IAddressRepository _addressRepository = null!;
    private AddressPostValidator _postValidator = null!;
    private AddressPutValidator _putValidator = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _addressRepository = Substitute.For<IAddressRepository>();
        _postValidator = new AddressPostValidator(_clientRepository, _addressRepository);
        _putValidator = new AddressPutValidator(_clientRepository, _addressRepository);
    }

    private AddressResource AddressFor(EntityTypeEnum ownerType, AddressTypeEnum addressType)
    {
        var clientId = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(ownerType, "owner"));
        return new AddressResource { Id = Guid.NewGuid(), ClientId = clientId, Type = addressType };
    }

    [Test]
    public async Task Post_AllowedType_IsValid()
    {
        var address = AddressFor(EntityTypeEnum.Customer, AddressTypeEnum.InvoicingAddress);

        var result = await _postValidator.ValidateAsync(new PostCommand<AddressResource>(address));

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Post_DisallowedType_IsRejected()
    {
        var address = AddressFor(EntityTypeEnum.Employee, AddressTypeEnum.Workplace);

        var result = await _postValidator.ValidateAsync(new PostCommand<AddressResource>(address));

        Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Member(ClientAddressTypeRules.NotAllowedMessage));
    }

    [Test]
    public async Task Post_UnknownOwner_IsLeftToTheHandler()
    {
        var address = new AddressResource { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Type = AddressTypeEnum.InvoicingAddress };

        var result = await _postValidator.ValidateAsync(new PostCommand<AddressResource>(address));

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Put_UnchangedLegacyType_IsAccepted()
    {
        var address = AddressFor(EntityTypeEnum.Employee, AddressTypeEnum.Workplace);
        _addressRepository.GetNoTracking(address.Id).Returns(new Address { Id = address.Id, Type = AddressTypeEnum.Workplace });

        var result = await _putValidator.ValidateAsync(new PutCommand<AddressResource>(address));

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public async Task Put_RetypedToDisallowedType_IsRejected()
    {
        var address = AddressFor(EntityTypeEnum.Employee, AddressTypeEnum.Workplace);
        _addressRepository.GetNoTracking(address.Id).Returns(new Address { Id = address.Id, Type = AddressTypeEnum.Employee });

        var result = await _putValidator.ValidateAsync(new PutCommand<AddressResource>(address));

        Assert.That(result.IsValid, Is.False);
    }
}
