// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The person data of a stand-in conversion: the target is an employee or an external employee, first name, name and a
/// person gender are required with the messages of the client POST, the address is optional, but when given its type
/// must suit the target type and it needs the date it is valid from.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class ConvertStandInCommandValidatorTests
{
    private ConvertStandInCommandValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new ConvertStandInCommandValidator(
            Substitute.For<IGeocodingService>(),
            new StateAbbreviationResolver(Substitute.For<IStateRepository>()),
            Substitute.For<ICountryResolver>(),
            Substitute.For<IAddressRepository>(),
            NullLogger<AddressGeocodingValidator>.Instance);
    }

    private static ConvertStandInResource Valid(EntityTypeEnum target = EntityTypeEnum.Employee) => new()
    {
        TargetType = (int)target,
        FirstName = "Anna",
        Name = "Muster",
        Gender = GenderEnum.Female,
    };

    private Task<FluentValidation.Results.ValidationResult> ValidateAsync(ConvertStandInResource resource) =>
        _validator.ValidateAsync(new ConvertStandInCommand(Guid.NewGuid(), resource));

    [TestCase(EntityTypeEnum.Employee)]
    [TestCase(EntityTypeEnum.ExternEmp)]
    public async Task PersonData_WithoutAddressOrContact_IsValid(EntityTypeEnum target)
    {
        (await ValidateAsync(Valid(target))).Errors.ShouldBeEmpty();
    }

    [TestCase(EntityTypeEnum.Customer)]
    [TestCase(EntityTypeEnum.StandIn)]
    [TestCase((EntityTypeEnum)99)]
    public async Task TargetType_OtherThanEmployeeOrExternal_IsInvalid(EntityTypeEnum target)
    {
        var result = await ValidateAsync(Valid(target));

        result.Errors.ShouldContain(error => error.ErrorMessage == ConvertStandInCommandValidator.TargetTypeMessage);
    }

    [Test]
    public async Task MissingFirstNameNameAndGender_AreInvalid_WithTheMessagesOfTheClientPost()
    {
        var resource = Valid();
        resource.FirstName = " ";
        resource.Name = string.Empty;
        resource.Gender = GenderEnum.LegalEntity;

        var messages = (await ValidateAsync(resource)).Errors.Select(error => error.ErrorMessage).ToList();

        messages.ShouldContain(ClientPersonRules.FirstNameRequiredMessage);
        messages.ShouldContain(ClientPersonRules.NameRequiredMessage);
        messages.ShouldContain(ClientPersonRules.GenderRequiredMessage);
    }

    [Test]
    public async Task Address_OfATypeTheTargetMayNotCarry_IsInvalid()
    {
        var resource = Valid(EntityTypeEnum.Employee);
        resource.Address = new AddressResource { Type = AddressTypeEnum.InvoicingAddress, ValidFrom = DateTime.UtcNow };

        var result = await ValidateAsync(resource);

        result.Errors.ShouldContain(error => error.ErrorMessage == ClientAddressTypeRules.NotAllowedMessage);
    }

    [Test]
    public async Task Address_OfAnAllowedType_IsValid()
    {
        var resource = Valid(EntityTypeEnum.ExternEmp);
        resource.Address = new AddressResource { Type = AddressTypeEnum.Workplace, ValidFrom = DateTime.UtcNow };

        (await ValidateAsync(resource)).Errors.ShouldBeEmpty();
    }

    [Test]
    public async Task Address_WithoutValidFrom_IsInvalid()
    {
        var resource = Valid();
        resource.Address = new AddressResource { Type = AddressTypeEnum.Employee };

        var result = await ValidateAsync(resource);

        result.Errors.ShouldContain(error => error.ErrorMessage == ConvertStandInCommandValidator.AddressValidFromMessage);
    }

    [Test]
    public async Task Address_ForAnUndefinedTarget_RefusesTheTargetOnly_WithoutThrowing()
    {
        var resource = Valid((EntityTypeEnum)99);
        resource.Address = new AddressResource { Type = AddressTypeEnum.Employee, ValidFrom = DateTime.UtcNow };

        var result = await ValidateAsync(resource);

        result.Errors.Select(error => error.ErrorMessage).ShouldBe([ConvertStandInCommandValidator.TargetTypeMessage]);
    }

    [Test]
    public async Task Address_WithAnId_IsInvalid_SoTheGeocodingCheckNeverTakesItForAStoredOne()
    {
        var resource = Valid();
        resource.Address = new AddressResource { Id = Guid.NewGuid(), Type = AddressTypeEnum.Employee, ValidFrom = DateTime.UtcNow };

        var result = await ValidateAsync(resource);

        result.Errors.ShouldContain(error => error.ErrorMessage == ConvertStandInCommandValidator.AddressIdMessage);
    }

    [Test]
    public async Task Names_LongerThanTheirColumns_AreInvalid()
    {
        var resource = Valid();
        resource.FirstName = new string('a', ClientFieldLimits.NameMaxLength + 1);
        resource.Name = new string('b', ClientFieldLimits.NameMaxLength + 1);

        var result = await ValidateAsync(resource);

        result.Errors.Count(error => error.ErrorMessage.StartsWith("First name and name have at most", StringComparison.Ordinal)).ShouldBe(2);
    }
}
