// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that CustomSttProviderController keeps masking the API key as "***" in every
/// response after the at-rest encryption change, and that Update preserves the stored key
/// when the client echoes back the masked sentinel instead of a real value.
/// </summary>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.DTOs.Assistant;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Assistant;

[TestFixture]
public class CustomSttProviderControllerTests
{
    private const string MaskedApiKey = "***";

    private ICustomSttProviderRepository _repository = null!;
    private CustomSttProviderController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ICustomSttProviderRepository>();
        _controller = new CustomSttProviderController(_repository);
    }

    [Test]
    public async Task GetAll_ProviderWithStoredApiKey_MasksKeyInResponse()
    {
        var provider = new CustomSttProvider
        {
            Id = Guid.NewGuid(),
            Name = "Self-hosted Whisper",
            ConnectionType = "rest",
            ApiUrl = "https://stt.example.test/v1",
            ApiKey = "sk-actual-plaintext-key-from-decrypted-entity",
            IsEnabled = true,
        };
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<CustomSttProvider> { provider });

        var result = await _controller.GetAll(CancellationToken.None);

        var dtos = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<List<CustomSttProviderDto>>();
        dtos.ShouldHaveSingleItem().ApiKey.ShouldBe(MaskedApiKey);
    }

    [Test]
    public async Task Create_WithPlainApiKey_PassesPlainValueToRepositoryButMasksResponse()
    {
        const string plainApiKey = "sk-brand-new-provider-key";
        var dto = new CustomSttProviderDto(Guid.Empty, "New Provider", "rest", "https://stt.example.test/v1", plainApiKey, null, true, false);

        var result = await _controller.Create(dto, CancellationToken.None);

        await _repository.Received(1).AddAsync(
            Arg.Is<CustomSttProvider>(p => p.ApiKey == plainApiKey),
            Arg.Any<CancellationToken>());

        var created = result.ShouldBeOfType<CreatedAtActionResult>().Value.ShouldBeOfType<CustomSttProviderDto>();
        created.ApiKey.ShouldBe(MaskedApiKey);
    }

    [Test]
    public async Task Update_WhenClientEchoesMaskedSentinel_KeepsExistingApiKeyUnchanged()
    {
        var providerId = Guid.NewGuid();
        const string existingPlainApiKey = "sk-existing-unchanged-key";
        var existing = new CustomSttProvider
        {
            Id = providerId,
            Name = "Provider",
            ConnectionType = "rest",
            ApiUrl = "https://stt.example.test/v1",
            ApiKey = existingPlainApiKey,
            IsEnabled = true,
        };
        _repository.GetByIdAsync(providerId, Arg.Any<CancellationToken>()).Returns(existing);
        var dto = new CustomSttProviderDto(providerId, "Provider", "rest", "https://stt.example.test/v1", MaskedApiKey, null, true, false);

        var result = await _controller.Update(providerId, dto, CancellationToken.None);

        await _repository.Received(1).UpdateAsync(
            Arg.Is<CustomSttProvider>(p => p.ApiKey == existingPlainApiKey),
            Arg.Any<CancellationToken>());

        var updated = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CustomSttProviderDto>();
        updated.ApiKey.ShouldBe(MaskedApiKey);
    }

    [Test]
    public async Task Update_WhenClientSendsNewApiKey_OverwritesStoredKeyAndMasksResponse()
    {
        var providerId = Guid.NewGuid();
        const string newPlainApiKey = "sk-replacement-key";
        var existing = new CustomSttProvider
        {
            Id = providerId,
            Name = "Provider",
            ConnectionType = "rest",
            ApiUrl = "https://stt.example.test/v1",
            ApiKey = "sk-old-key-to-be-replaced",
            IsEnabled = true,
        };
        _repository.GetByIdAsync(providerId, Arg.Any<CancellationToken>()).Returns(existing);
        var dto = new CustomSttProviderDto(providerId, "Provider", "rest", "https://stt.example.test/v1", newPlainApiKey, null, true, false);

        var result = await _controller.Update(providerId, dto, CancellationToken.None);

        await _repository.Received(1).UpdateAsync(
            Arg.Is<CustomSttProvider>(p => p.ApiKey == newPlainApiKey),
            Arg.Any<CancellationToken>());

        var updated = result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<CustomSttProviderDto>();
        updated.ApiKey.ShouldBe(MaskedApiKey);
    }
}
