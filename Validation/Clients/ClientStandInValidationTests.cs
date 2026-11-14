// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The client POST and PUT validators for a stand-in: the display name (Name) is enough, first name and gender are not
/// required, and an address, a communication or a contract is refused. An undefined client type is refused on both
/// paths, and an employee still needs first name and gender.
/// </summary>

using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class ClientStandInValidationTests
{
    private const int UndefinedType = 99;

    private PostCommandValidator _postValidator = null!;
    private PutCommandValidator _putValidator = null!;

    [SetUp]
    public void Setup()
    {
        var geocoding = Substitute.For<IGeocodingService>();
        var countryResolver = Substitute.For<ICountryResolver>();
        var addressRepository = Substitute.For<IAddressRepository>();
        var clientRepository = Substitute.For<IClientRepository>();
        var stateResolver = new StateAbbreviationResolver(Substitute.For<IStateRepository>());
        var shiftRepository = Substitute.For<IShiftRepository>();

        _postValidator = new PostCommandValidator(geocoding, stateResolver, countryResolver, addressRepository, clientRepository, NullLogger<AddressGeocodingValidator>.Instance);
        _putValidator = new PutCommandValidator(
            geocoding, stateResolver, countryResolver, addressRepository, shiftRepository, clientRepository,
            TestGroupWriteVisibility.AllClientsVisible(), TestGroupWriteVisibility.UnrestrictedGroups(),
            NullLogger<AddressGeocodingValidator>.Instance);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StandIn_NameOnly_IsValid(bool viaPut)
    {
        var result = await ValidateAsync(StandIn(), viaPut);

        result.Errors.ShouldBeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StandIn_WithoutName_IsInvalid(bool viaPut)
    {
        var resource = StandIn();
        resource.Name = string.Empty;

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe([ClientPersonRules.NameRequiredMessage]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StandIn_WithAddress_IsInvalid(bool viaPut)
    {
        var resource = StandIn();
        resource.Addresses.Add(new AddressResource { Type = AddressTypeEnum.Employee, Street = "Main 1" });

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe([ClientAddressTypeRules.NotAllowedMessage]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StandIn_WithCommunication_IsInvalid(bool viaPut)
    {
        var resource = StandIn();
        resource.Communications.Add(new CommunicationResource { Type = CommunicationTypeEnum.PrivateMail, Value = "a@b.c" });

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe([ClientTypeValidator.StandInCommunicationsMessage]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StandIn_WithContract_IsInvalid(bool viaPut)
    {
        var resource = StandIn();
        resource.ClientContracts.Add(new ClientContractResource { ContractId = Guid.NewGuid(), IsActive = true, FromDate = new DateOnly(2026, 11, 1) });

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe([ClientTypeValidator.StandInContractsMessage]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Type_99_IsInvalid_EvenWithAnAddress(bool viaPut)
    {
        var resource = Employee();
        resource.Type = UndefinedType;
        resource.Addresses.Add(new AddressResource { Type = AddressTypeEnum.Employee, Street = "Main 1" });

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe([ClientTypeValidator.InvalidTypeMessage]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Employee_WithoutFirstNameAndGender_StaysInvalid(bool viaPut)
    {
        var resource = Employee();
        resource.FirstName = string.Empty;
        resource.Gender = GenderEnum.LegalEntity;

        var result = await ValidateAsync(resource, viaPut);

        ErrorMessages(result).ShouldBe(
            [ClientPersonRules.FirstNameRequiredMessage, ClientPersonRules.GenderRequiredMessage], ignoreOrder: true);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Employee_Complete_IsValid(bool viaPut)
    {
        var result = await ValidateAsync(Employee(), viaPut);

        result.Errors.ShouldBeEmpty();
    }

    private async Task<ValidationResult> ValidateAsync(ClientResource resource, bool viaPut) =>
        viaPut
            ? await _putValidator.ValidateAsync(new PutCommand<ClientResource>(resource))
            : await _postValidator.ValidateAsync(new PostCommand<ClientResource>(resource));

    private static List<string> ErrorMessages(ValidationResult result) =>
        result.Errors.Select(error => error.ErrorMessage).ToList();

    private static ClientResource StandIn() => new()
    {
        Name = "Fair 1",
        FirstName = string.Empty,
        Type = (int)EntityTypeEnum.StandIn,
        SkipAddressValidation = true,
    };

    private static ClientResource Employee() => new()
    {
        Name = "Muster",
        FirstName = "Anna",
        Gender = GenderEnum.Female,
        Type = (int)EntityTypeEnum.Employee,
        SkipAddressValidation = true,
    };
}
