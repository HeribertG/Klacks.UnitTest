// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the id-based lookups of ClientResolver: a client hidden by group visibility reads
/// exactly like a missing one and is never loaded from the repository.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClientResolverVisibilityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Anna", Name = "Muster" });
        _clientRepository.Exists(ClientId).Returns(true);
    }

    [Test]
    public async Task LoadVisibleByIdAsync_VisibleClient_IsLoaded()
    {
        var client = await ClientResolver.LoadVisibleByIdAsync(
            _clientRepository, SkillClientVisibility.AllVisible(), ClientId, CancellationToken.None);

        client.ShouldNotBeNull();
        client.Id.ShouldBe(ClientId);
    }

    [Test]
    public async Task LoadVisibleByIdAsync_HiddenClient_ReturnsNull_WithoutLoading()
    {
        var client = await ClientResolver.LoadVisibleByIdAsync(
            _clientRepository, SkillClientVisibility.Hiding(ClientId), ClientId, CancellationToken.None);

        client.ShouldBeNull();
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task LoadVisibleByIdAsync_UnknownClient_ReturnsNull()
    {
        var unknownId = Guid.NewGuid();
        _clientRepository.Get(unknownId).Returns((Client?)null);

        var client = await ClientResolver.LoadVisibleByIdAsync(
            _clientRepository, SkillClientVisibility.AllVisible(), unknownId, CancellationToken.None);

        client.ShouldBeNull();
    }

    [Test]
    public async Task ExistsVisibleAsync_VisibleClient_IsTrue()
    {
        var exists = await ClientResolver.ExistsVisibleAsync(
            _clientRepository, SkillClientVisibility.AllVisible(), ClientId, CancellationToken.None);

        exists.ShouldBeTrue();
    }

    [Test]
    public async Task ExistsVisibleAsync_HiddenClient_IsFalse_WithoutQueryingExistence()
    {
        var exists = await ClientResolver.ExistsVisibleAsync(
            _clientRepository, SkillClientVisibility.Hiding(ClientId), ClientId, CancellationToken.None);

        exists.ShouldBeFalse();
        await _clientRepository.DidNotReceive().Exists(Arg.Any<Guid>());
    }
}
