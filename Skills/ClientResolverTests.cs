// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientResolver, driven by the 2026-07-07 skill-usage failure classes: genuine
/// duplicate names (same first and last name, different client numbers) are resolvable via the
/// optional idNumber parameter and never by silently picking one, the ambiguity error tells the
/// model to retry with idNumber, an exact full-name match is preferred over looser search hits,
/// and the legacy overload keeps its self-complete instruction without advertising idNumber.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClientResolverTests
{
    private IClientSearchRepository _searchRepository = null!;
    private IClientRepository _clientRepository = null!;

    private static readonly Guid FirstDuplicateId = Guid.NewGuid();
    private static readonly Guid SecondDuplicateId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
    }

    private void SearchReturns(params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private void SearchReturnsDuplicateLya()
    {
        SearchReturns(
            new ClientSearchItem { Id = FirstDuplicateId, FirstName = "Lya", LastName = "Ackermann", IdNumber = 1238 },
            new ClientSearchItem { Id = SecondDuplicateId, FirstName = "Lya", LastName = "Ackermann", IdNumber = 1450 });
    }

    [Test]
    public async Task DuplicateNames_WithIdNumber_ResolvesThatExactClient()
    {
        SearchReturnsDuplicateLya();
        _clientRepository.Get(SecondDuplicateId)
            .Returns(new Client { Id = SecondDuplicateId, FirstName = "Lya", Name = "Ackermann" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", 1450, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(SecondDuplicateId));
    }

    [Test]
    public async Task DuplicateNames_WithoutIdNumber_ErrorNamesTheIdNumberParameterAndCandidates()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("Multiple clients match"));
        Assert.That(error, Does.Contain("#1238"));
        Assert.That(error, Does.Contain("#1450"));
        Assert.That(error, Does.Contain(ClientResolver.IdNumberParameterName));
        Assert.That(error, Does.Contain("edit page"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task DuplicateNames_WithUnknownIdNumber_ListsTheRealClientNumbers()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", 9999, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("9999"));
        Assert.That(error, Does.Contain("#1238"));
        Assert.That(error, Does.Contain("#1450"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task LegacyOverload_KeepsSelfCompleteInstruction_WithoutAdvertisingIdNumber()
    {
        SearchReturnsDuplicateLya();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("complete the requested action"));
        Assert.That(error, Does.Not.Contain(ClientResolver.IdNumberParameterName));
    }

    [Test]
    public async Task ExactFullNameMatch_IsPreferredOverLooserSearchHits()
    {
        var exactId = Guid.NewGuid();
        SearchReturns(
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Heribert", LastName = "E2EGasparoli348927", IdNumber = 2424 },
            new ClientSearchItem { Id = exactId, FirstName = "Heribert", LastName = "Gasparoli", IdNumber = 2667 });
        _clientRepository.Get(exactId)
            .Returns(new Client { Id = exactId, FirstName = "Heribert", Name = "Gasparoli" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Heribert", "Gasparoli", null, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(exactId));
    }

    [Test]
    public async Task NoMatch_TellsModelNotToRetryWithTheSameName()
    {
        SearchReturns();

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Nemo", "Niemand", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("No client found matching"));
        Assert.That(error, Does.Contain("do not call this skill again with the same name"));
    }
}
