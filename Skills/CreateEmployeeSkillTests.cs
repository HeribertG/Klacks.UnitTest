// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateEmployeeSkill — the mandatory-onboarding-data guard (address/email/phone),
/// the explicit "user declined" escape (proceedWithoutContact) and the complete happy path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateEmployeeSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICountryResolver _countryResolver = null!;
    private CreateEmployeeSkill _skill = null!;

    private static Countries MakeCountry(string abbr, string prefix, string nameDe, string nameEn) => new()
    {
        Abbreviation = abbr,
        Prefix = prefix,
        Name = new MultiLanguage { De = nameDe, En = nameEn }
    };

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _countryResolver = Substitute.For<ICountryResolver>();

        var ch = MakeCountry("CH", "+41", "Schweiz", "Switzerland");
        var de = MakeCountry("DE", "+49", "Deutschland", "Germany");

        _countryResolver.ResolveAsync("CH", Arg.Any<CancellationToken>()).Returns(ch);
        _countryResolver.ResolveAsync("DE", Arg.Any<CancellationToken>()).Returns(de);
        _countryResolver.ResolveAsync(Arg.Is<string?>(s => string.IsNullOrWhiteSpace(s)), Arg.Any<CancellationToken>())
            .Returns((Countries?)null);
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(ch);

        _skill = new CreateEmployeeSkill(_clientRepository, _searchRepository, _unitOfWork, _countryResolver);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanCreateClients" }
    };

    private static Dictionary<string, object> CompleteParameters() => new()
    {
        ["firstName"] = "Heribert",
        ["lastName"] = "Gasparoli",
        ["gender"] = "Male",
        ["street"] = "Bahnhofstrasse 1",
        ["zip"] = "3097",
        ["city"] = "Liebefeld",
        ["email"] = "heribert@example.com",
        ["phone"] = "+41 79 123 45 67"
    };

    [Test]
    public async Task ReturnsError_AndDoesNotPersist_WhenAddressEmailAndPhoneMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Heribert",
            ["lastName"] = "Gasparoli",
            ["gender"] = "Male"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("address").And.Contain("email").And.Contain("phone"));
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_WhenOnlyEmailMissing()
    {
        var parameters = CompleteParameters();
        parameters.Remove("email");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("email"));
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
    }

    [Test]
    public async Task ReturnsError_WhenOnlyPhoneMissing()
    {
        var parameters = CompleteParameters();
        parameters.Remove("phone");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("phone"));
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
    }

    [Test]
    public async Task ReturnsError_WhenAddressIncomplete()
    {
        var parameters = CompleteParameters();
        parameters.Remove("zip");

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("address"));
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
    }

    [Test]
    public async Task CreatesClient_WhenProceedWithoutContactIsTrue_DespiteMissingContact()
    {
        Client? captured = null;
        await _clientRepository.Add(Arg.Do<Client>(c => captured = c));

        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Heribert",
            ["lastName"] = "Gasparoli",
            ["gender"] = "Male",
            ["proceedWithoutContact"] = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Add(Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Addresses, Is.Empty);
        Assert.That(captured.Communications, Is.Empty);
        Assert.That(captured.Membership, Is.Not.Null);
    }

    [Test]
    public async Task CreatesClient_WithAddressAndCommunications_WhenDataComplete()
    {
        Client? captured = null;
        await _clientRepository.Add(Arg.Do<Client>(c => captured = c));

        var result = await _skill.ExecuteAsync(Ctx(), CompleteParameters());

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Add(Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.FirstName, Is.EqualTo("Heribert"));
        Assert.That(captured.Name, Is.EqualTo("Gasparoli"));
        Assert.That(captured.Gender, Is.EqualTo(GenderEnum.Male));
        Assert.That(captured.Addresses, Has.Count.EqualTo(1));
        Assert.That(captured.Communications, Has.Count.EqualTo(2));
        Assert.That(captured.Communications.Any(c => c.Type == CommunicationTypeEnum.PrivateMail), Is.True);
        Assert.That(captured.Communications.Any(c => c.Type == CommunicationTypeEnum.PrivateCellPhone), Is.True);
        Assert.That(captured.Membership, Is.Not.Null);
    }

    [TestCase("", "CH")]
    [TestCase("CH", "CH")]
    [TestCase("DE", "DE")]
    public async Task DefaultsCountryToCh_AndKeepsProvidedCode(string input, string expected)
    {
        Client? captured = null;
        await _clientRepository.Add(Arg.Do<Client>(c => captured = c));
        var parameters = CompleteParameters();
        parameters["country"] = input;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(captured!.Addresses.Single().Country, Is.EqualTo(expected));
    }

    [Test]
    public async Task DerivesStateFromZip_WhenStateMissing()
    {
        _searchRepository.FindStatePostCode("3097").Returns("BE");
        Client? captured = null;
        await _clientRepository.Add(Arg.Do<Client>(c => captured = c));
        var parameters = CompleteParameters();

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(captured!.Addresses.Single().State, Is.EqualTo("BE"));
    }

    [TestCase("0791021402", "CH", "+41", "791021402")]
    [TestCase("+41 79 102 14 02", "CH", "+41", "791021402")]
    [TestCase("0044 20 7946 0958", "CH", "", "+442079460958")]
    public async Task SplitsPhoneIntoPrefixAndNumber(string input, string country, string expectedPrefix, string expectedValue)
    {
        Client? captured = null;
        await _clientRepository.Add(Arg.Do<Client>(c => captured = c));
        var parameters = CompleteParameters();
        parameters["phone"] = input;
        parameters["country"] = country;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var phone = captured!.Communications.Single(c => c.Type == CommunicationTypeEnum.PrivateCellPhone);
        Assert.That(phone.Prefix, Is.EqualTo(expectedPrefix));
        Assert.That(phone.Value, Is.EqualTo(expectedValue));
    }
}
