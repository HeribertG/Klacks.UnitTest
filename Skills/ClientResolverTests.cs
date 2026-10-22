// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ClientResolver, driven by the 2026-07-07 skill-usage failure classes: genuine
/// duplicate names (same first and last name, different client numbers) are resolvable via the
/// optional idNumber parameter and never by silently picking one, the ambiguity error tells the
/// model to retry with idNumber, an exact full-name match is preferred over looser search hits,
/// and the legacy overload keeps its self-complete instruction without advertising idNumber.
/// Under the default Strict policy only a client the user really named is resolved (equal words or whole words,
/// first and last name in either order); a row that the text search found inside a word, or that only the fuzzy
/// search found ("Meier" for the only "Mayer"), a nickname and a diacritic the stored name lacks are answered with
/// the name ask-back and nothing is loaded. Lenient (read-only skills) still takes the lone row.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Klacks.Api.Infrastructure.Services.Clients;

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
        FuzzySearchReturns();
    }

    private void FuzzySearchReturns(params ClientSearchItem[] items)
    {
        _searchRepository.SearchByFuzzyNameAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private static ClientSearchItem Person(Guid id, string firstName, string lastName, int idNumber) =>
        new() { Id = id, FirstName = firstName, LastName = lastName, IdNumber = idNumber };

    private async Task AssertAskedBackWithoutLoading(string? error, string suggestedName)
    {
        Assert.That(error, Does.Contain("Did the user mean '" + suggestedName));
        Assert.That(NameResolutionAskBack.IsAskBack(error), Is.True);
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task FuzzyOnlySingleHit_IsAskedBack_AndNeverLoaded()
    {
        SearchReturns();
        FuzzySearchReturns(Person(FirstDuplicateId, "Hans", "Mayer", 12));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Hans", "Meier", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        await AssertAskedBackWithoutLoading(error, "Hans Mayer (#12)");
    }

    [Test]
    public async Task FuzzyOnlySingleHit_IsTakenUnderLenient()
    {
        SearchReturns();
        FuzzySearchReturns(Person(FirstDuplicateId, "Hans", "Mayer", 12));
        _clientRepository.Get(FirstDuplicateId)
            .Returns(new Client { Id = FirstDuplicateId, FirstName = "Hans", Name = "Mayer" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Hans", "Meier", null, CancellationToken.None,
            policy: NameMatchPolicy.Lenient);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(FirstDuplicateId));
    }

    [Test]
    public async Task SeveralFuzzyHits_AreAskedBackAsAList()
    {
        SearchReturns();
        FuzzySearchReturns(
            Person(FirstDuplicateId, "Hans", "Mayer", 12),
            Person(SecondDuplicateId, "Hans", "Maier", 13));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Hans", "Meier", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("#12"));
        Assert.That(error, Does.Contain("#13"));
        Assert.That(NameResolutionAskBack.IsAskBack(error), Is.True);
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task TextHitInsideAWord_IsAskedBack()
    {
        SearchReturns(Person(FirstDuplicateId, "Peter", "Meierhans", 21));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, null, "Meier", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        await AssertAskedBackWithoutLoading(error, "Peter Meierhans (#21)");
    }

    [Test]
    public async Task Nickname_IsAskedBack()
    {
        SearchReturns(Person(FirstDuplicateId, "Johannes", "Meier", 22));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Hans", "Meier", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        await AssertAskedBackWithoutLoading(error, "Johannes Meier (#22)");
    }

    [TestCase("Hans", "Meier")]
    [TestCase("Meier", "Hans")]
    [TestCase(null, "Meier")]
    public async Task NamedClient_Resolves_InEitherNameOrder(string? firstName, string lastName)
    {
        SearchReturns(Person(FirstDuplicateId, "Hans", "Meier", 23));
        _clientRepository.Get(FirstDuplicateId)
            .Returns(new Client { Id = FirstDuplicateId, FirstName = "Hans", Name = "Meier" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, firstName, lastName, null, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(FirstDuplicateId));
    }

    [Test]
    public async Task QueryWithoutDiacritics_ResolvesTheAccentedName()
    {
        SearchReturns();
        FuzzySearchReturns(Person(FirstDuplicateId, "Vreni", "Müller", 24));
        _clientRepository.Get(FirstDuplicateId)
            .Returns(new Client { Id = FirstDuplicateId, FirstName = "Vreni", Name = "Müller" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Vreni", "Muller", null, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(FirstDuplicateId));
    }

    [Test]
    public async Task QueryWithADiacriticTheStoredNameLacks_IsAskedBack()
    {
        SearchReturns();
        FuzzySearchReturns(Person(FirstDuplicateId, "Vreni", "Muller", 25));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Vreni", "Müller", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        await AssertAskedBackWithoutLoading(error, "Vreni Muller (#25)");
    }

    [Test]
    public async Task NamedClientWins_OverASimilarNameInTheSameResult()
    {
        SearchReturns(
            Person(FirstDuplicateId, "Hans", "Meier", 26),
            Person(SecondDuplicateId, "Hans", "Meierhans", 27));
        _clientRepository.Get(FirstDuplicateId)
            .Returns(new Client { Id = FirstDuplicateId, FirstName = "Hans", Name = "Meier" });

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Hans", "Meier", null, CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(client!.Id, Is.EqualTo(FirstDuplicateId));
    }

    [Test]
    public async Task DuplicateNames_AmbiguityIsANameAskBack()
    {
        SearchReturnsDuplicateLya();

        var (_, error) = await ClientResolver.ResolveByNameAsync(
            _searchRepository, _clientRepository, "Lya", "Ackermann", null, CancellationToken.None);

        Assert.That(NameResolutionAskBack.IsAskBack(error), Is.True);
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

    // ClientResolver is the entry point of every name-based write skill (add phone/email/note,
    // assign contract, remove from group/qualification). It never validates visibility itself —
    // its whole guarantee is that it only ever loads an id that came out of SearchAsync. This
    // test wires the real search repository behind it so that guarantee is checked end to end
    // instead of assumed: a restricted caller must not be able to mutate a foreign client.
    [Test]
    public async Task RestrictedUser_CannotResolveClientOfAnInvisibleGroup_AndNeverLoadsIt()
    {
        var visibleGroupId = Guid.NewGuid();
        var invisibleGroupId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        using var dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        dbContext.Database.EnsureCreated();

        var foreignClientId = Guid.NewGuid();
        dbContext.Client.Add(new Client
        {
            Id = foreignClientId,
            FirstName = "Petra",
            Name = "Steinmann",
            IdNumber = 9101,
            Type = EntityTypeEnum.Employee
        });
        dbContext.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = foreignClientId,
            GroupId = invisibleGroupId,
            AnalyseToken = null
        });
        dbContext.SaveChanges();

        var groupVisibility = Substitute.For<IGroupVisibilityService>();
        groupVisibility.GetVisibilityScopeAsync()
            .Returns(GroupVisibilityScope.Restricted(new[] { visibleGroupId }, new[] { visibleGroupId }));
        var userService = Substitute.For<IUserService>();
        userService.GetIdString().Returns("restricted-user");

        var fuzzySearchService = Substitute.For<IClientFuzzySearchService>();
        fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());

        var searchRepository = new ClientSearchRepository(
            dbContext,
            new ClientGroupFilterService(
                Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>(),
                groupVisibility,
                userService,
                Substitute.For<ILogger<ClientGroupFilterService>>()),
            fuzzySearchService,
            new Klacks.UnitTest.TestHelpers.FixedCompanyClock(DateTimeOffset.UtcNow));

        var (client, error) = await ClientResolver.ResolveByNameAsync(
            searchRepository, _clientRepository, "Petra", "Steinmann", null, CancellationToken.None);

        Assert.That(client, Is.Null);
        Assert.That(error, Does.Contain("No client found matching"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }
}
