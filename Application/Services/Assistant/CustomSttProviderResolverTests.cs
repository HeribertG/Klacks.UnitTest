// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CustomSttProviderResolver: only ids with the custom prefix and a valid guid resolve, the
/// provider must exist and be enabled, and anything else (built-in ids, malformed guid, unknown or disabled
/// row) yields no provider.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Shouldly;

[TestFixture]
public class CustomSttProviderResolverTests
{
    private ICustomSttProviderRepository _repository = null!;
    private CustomSttProviderResolver _resolver = null!;

    private static string CustomId(Guid id) => $"{SttProviderConstants.CustomProviderPrefix}{id}";

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ICustomSttProviderRepository>();
        _resolver = new CustomSttProviderResolver(_repository);
    }

    [Test]
    public async Task TryResolveAsync_ReturnsTheProvider_WhenCustomIdPointsToAnEnabledRow()
    {
        var provider = new CustomSttProvider { Id = Guid.NewGuid(), IsEnabled = true };
        _repository.GetByIdAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        var result = await _resolver.TryResolveAsync(CustomId(provider.Id));

        result.ShouldBe(provider);
    }

    [Test]
    public async Task TryResolveAsync_ReturnsNull_WhenTheRowIsDisabled()
    {
        var provider = new CustomSttProvider { Id = Guid.NewGuid(), IsEnabled = false };
        _repository.GetByIdAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        (await _resolver.TryResolveAsync(CustomId(provider.Id))).ShouldBeNull();
    }

    [Test]
    public async Task TryResolveAsync_ReturnsNull_WhenTheRowDoesNotExist()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((CustomSttProvider?)null);

        (await _resolver.TryResolveAsync(CustomId(Guid.NewGuid()))).ShouldBeNull();
    }

    [TestCase(SttProviderConstants.Browser)]
    [TestCase(SttProviderConstants.GroqWhisper)]
    [TestCase("custom:not-a-guid")]
    [TestCase("custom:")]
    public async Task TryResolveAsync_ReturnsNullWithoutQuerying_ForIdsThatAreNotResolvableCustomProviders(string providerId)
    {
        (await _resolver.TryResolveAsync(providerId)).ShouldBeNull();

        await _repository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestCase("custom:abc", true)]
    [TestCase("CUSTOM:abc", true)]
    [TestCase("groq-whisper", false)]
    [TestCase("browser", false)]
    public void IsCustomProviderId_RecognisesThePrefixIgnoringCase(string providerId, bool expected)
    {
        _resolver.IsCustomProviderId(providerId).ShouldBe(expected);
    }
}
