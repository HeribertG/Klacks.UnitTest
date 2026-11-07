// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;

using CommunicationPostValidator = Klacks.Api.Application.Validation.Communications.PostCommandValidator;
using CommunicationPutValidator = Klacks.Api.Application.Validation.Communications.PutCommandValidator;

namespace Klacks.UnitTest.Validation.Communications;

[TestFixture]
public class CommunicationStandInValidatorTests
{
    private IClientRepository _clientRepository = null!;
    private CommunicationPostValidator _postValidator = null!;
    private CommunicationPutValidator _putValidator = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _postValidator = new CommunicationPostValidator(_clientRepository);
        _putValidator = new CommunicationPutValidator(_clientRepository);
    }

    private CommunicationResource CommunicationFor(EntityTypeEnum ownerType)
    {
        var clientId = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(ownerType, "owner"));
        return new CommunicationResource
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Type = CommunicationTypeEnum.PrivateMail,
            Value = "owner@example.com"
        };
    }

    [Test]
    public async Task Post_ForStandIn_IsRefused()
    {
        var communication = CommunicationFor(EntityTypeEnum.StandIn);

        var result = await _postValidator.ValidateAsync(new PostCommand<CommunicationResource>(communication));

        Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Member(ClientTypeValidator.StandInCommunicationsMessage));
    }

    [Test]
    public async Task Put_ForStandIn_IsRefused()
    {
        var communication = CommunicationFor(EntityTypeEnum.StandIn);

        var result = await _putValidator.ValidateAsync(new PutCommand<CommunicationResource>(communication));

        Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Member(ClientTypeValidator.StandInCommunicationsMessage));
    }

    [TestCase(EntityTypeEnum.Employee)]
    [TestCase(EntityTypeEnum.ExternEmp)]
    [TestCase(EntityTypeEnum.Customer)]
    public async Task PostAndPut_ForPersonOrCustomer_AreValid(EntityTypeEnum ownerType)
    {
        var communication = CommunicationFor(ownerType);

        var post = await _postValidator.ValidateAsync(new PostCommand<CommunicationResource>(communication));
        var put = await _putValidator.ValidateAsync(new PutCommand<CommunicationResource>(communication));

        Assert.That(post.IsValid, Is.True);
        Assert.That(put.IsValid, Is.True);
    }

    [Test]
    public async Task Post_UnknownOwner_IsLeftToTheHandler()
    {
        var communication = new CommunicationResource { Id = Guid.NewGuid(), ClientId = Guid.NewGuid() };

        var result = await _postValidator.ValidateAsync(new PostCommand<CommunicationResource>(communication));

        Assert.That(result.IsValid, Is.True);
    }
}
