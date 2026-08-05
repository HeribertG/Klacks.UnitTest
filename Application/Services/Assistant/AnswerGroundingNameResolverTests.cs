// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Resolver contract: a candidate counts as a client name only when both tokens strictly match
/// the client's given and family name (either order, single edit from length four), a title word
/// plus surname never resolves, non-bigrams skip the search entirely and search failures resolve
/// to "not a client name" instead of failing the evaluation.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class AnswerGroundingNameResolverTests
{
    private IClientSearchRepository _searchRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _searchRepository = Substitute.For<IClientSearchRepository>();
        SearchReturns();
    }

    private void SearchReturns(params (string? First, string? Last)[] clients)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = clients.Select(c => new ClientSearchItem { FirstName = c.First, LastName = c.Last }).ToList(),
                TotalCount = clients.Length
            });
    }

    private AnswerGroundingNameResolver Resolver()
        => new(_searchRepository, NullLogger<AnswerGroundingNameResolver>.Instance);

    [Test]
    public async Task BothTokensMatchingFirstAndLastName_Resolves_EitherOrder()
    {
        SearchReturns(("Anna", "Meier"));

        (await Resolver().ResolveClientNamesAsync(["Anna Meier"])).ShouldBe(new[] { "Anna Meier" });
        (await Resolver().ResolveClientNamesAsync(["Meier Anna"])).ShouldBe(new[] { "Meier Anna" });
    }

    [Test]
    public async Task SingleEditAndAccents_StillResolve()
    {
        SearchReturns(("Anna", "Meier"));

        (await Resolver().ResolveClientNamesAsync(["Anne Meyer"])).ShouldBe(new[] { "Anne Meyer" });
    }

    [Test]
    public async Task TitleWordPlusSurname_DoesNotResolve()
    {
        SearchReturns(("Anna", "Meier"));

        (await Resolver().ResolveClientNamesAsync(["Frau Meier"])).ShouldBeEmpty();
    }

    [Test]
    public async Task NonBigramCandidate_SkipsTheSearch()
    {
        var resolved = await Resolver().ResolveClientNamesAsync(["Anna"]);

        resolved.ShouldBeEmpty();
        await _searchRepository.DidNotReceive().SearchAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SearchFailure_TreatsTheCandidateAsUnresolved()
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<ClientSearchResult>>(_ => throw new InvalidOperationException("db down"));

        (await Resolver().ResolveClientNamesAsync(["Anna Meier"])).ShouldBeEmpty();
    }

    [Test]
    public async Task CompanyOnlyHit_DoesNotResolve()
    {
        SearchReturns((null, null));

        (await Resolver().ResolveClientNamesAsync(["Anna Meier"])).ShouldBeEmpty();
    }
}
