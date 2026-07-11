// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using Klacks.Api.Application.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ClientNameCandidateGrounderTests
{
    private const int MaxCandidateTerms = 3;

    private IClientSearchRepository _repository = null!;
    private Klacks.Api.Application.Interfaces.IClientFuzzySearchService _fuzzySearchService = null!;
    private ILogger<ClientNameCandidateGrounder> _logger = null!;
    private ClientNameCandidateGrounder _grounder = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientSearchRepository>();
        _fuzzySearchService = Substitute.For<Klacks.Api.Application.Interfaces.IClientFuzzySearchService>();
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());
        _logger = Substitute.For<ILogger<ClientNameCandidateGrounder>>();
        _grounder = new ClientNameCandidateGrounder(_repository, _fuzzySearchService, _logger);
    }

    [Test]
    public void ExtractCandidateTerms_TitleMarker_YieldsFollowingToken()
    {
        var terms = ClientNameCandidateGrounder.ExtractCandidateTerms("Ändere die Nummer von Frau Müller");

        terms.ShouldContain("Müller");
        terms.ShouldContain("Frau Müller");
    }

    [Test]
    public void ExtractCandidateTerms_HyphenatedName_YieldsBigramAndHyphenCandidate()
    {
        var terms = ClientNameCandidateGrounder.ExtractCandidateTerms("Hans-Peter Brönnimann anrufen");

        terms.ShouldContain("Hans-Peter Brönnimann");
        terms.ShouldContain("Hans-Peter");
    }

    [Test]
    public void ExtractCandidateTerms_LowercaseWithoutMarkers_IsEmpty()
    {
        var terms = ClientNameCandidateGrounder.ExtractCandidateTerms("bitte zeige alle offenen dienste von morgen");

        terms.ShouldBeEmpty();
    }

    [Test]
    public void ExtractCandidateTerms_LimitsToThreeTerms()
    {
        var terms = ClientNameCandidateGrounder.ExtractCandidateTerms("Anna Meier Beat Keller Cyrill Dorn");

        terms.Count.ShouldBe(MaxCandidateTerms);
    }

    [Test]
    public void ExtractCandidateTerms_DeduplicatesCaseInsensitively()
    {
        var terms = ClientNameCandidateGrounder.ExtractCandidateTerms("Frau Müller und nochmals Frau Müller");

        terms.Count.ShouldBe(2);
        terms.ShouldContain("Frau Müller");
        terms.ShouldContain("Müller");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ExtractCandidateTerms_BlankMessage_IsEmpty(string? message)
    {
        ClientNameCandidateGrounder.ExtractCandidateTerms(message).ShouldBeEmpty();
    }

    [Test]
    public async Task GroundAsync_RepositoryHit_RendersGroundingBlock()
    {
        var result = new ClientSearchResult
        {
            Items = new List<ClientSearchItem>
            {
                new() { Id = Guid.NewGuid(), IdNumber = 990001, FirstName = "Vreni", LastName = "Müller" }
            }
        };
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(result);
        var context = new LLMContext { Message = "Ändere die Nummer von Frau Müller" };

        await _grounder.GroundAsync(context);

        context.EntityGroundingBlock.ShouldNotBeNull();
        context.EntityGroundingBlock!.ShouldContain("Müller, Vreni (#990001)");
        context.EntityGroundingBlock.ShouldContain("copy EXACTLY this spelling");
    }

    [Test]
    public async Task GroundAsync_RepositoryThrows_SwallowsAndLeavesBlockNull()
    {
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("search failed"));
        var context = new LLMContext { Message = "Ändere die Nummer von Frau Müller" };

        await Should.NotThrowAsync(() => _grounder.GroundAsync(context));

        context.EntityGroundingBlock.ShouldBeNull();
    }

    [Test]
    public async Task GroundAsync_LongLowercaseMessage_RepositoryNotCalled()
    {
        // Above the lowercase-scan token cap the bigram fallback stays off, so a long
        // lowercase message without markers still probes nothing.
        var context = new LLMContext
        {
            Message = "bitte zeige mir doch einmal alle offenen dienste von morgen und übermorgen im überblick an danke"
        };

        await _grounder.GroundAsync(context);

        await _repository.DidNotReceiveWithAnyArgs().SearchAsync();
        context.EntityGroundingBlock.ShouldBeNull();
    }

    [Test]
    public async Task GroundAsync_ShortLowercaseVoiceMessage_GroundsViaBigramFallback()
    {
        // Voice STT delivers names all-lowercase; the trailing-bigram fallback must still
        // ground "petra meier" under the strict (exact/phonetic) validation.
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = new List<ClientSearchItem>() });
        _repository.SearchAsync(
                "petra meier", Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = Guid.NewGuid(), IdNumber = 990002, FirstName = "Petra", LastName = "Meier" }
                }
            });
        var context = new LLMContext { Message = "suche petra meier" };

        await _grounder.GroundAsync(context);

        context.EntityGroundingBlock.ShouldNotBeNull();
        context.EntityGroundingBlock!.ShouldContain("Meier, Petra (#990002)");
    }

    [Test]
    public async Task GroundAsync_ShortLowercaseMessage_StrictValidation_RejectsTrigramNoise()
    {
        // Fuzzy candidates for arbitrary lowercase bigrams must not leak into the prompt
        // unless they match exactly or phonetically.
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = new List<ClientSearchItem>() });
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = Guid.NewGuid(), FirstName = "Andreas", Name = "Zimmermann" } });
        var context = new LLMContext { Message = "nein das will ich nicht" };

        await _grounder.GroundAsync(context);

        context.EntityGroundingBlock.ShouldBeNull();
    }

    [Test]
    public async Task GroundAsync_ContainsMiss_PhoneticFuzzyFallback_GroundsMisheardName()
    {
        // The 2026-07-11 core case: the misheard "Meier" finds the stored "Mayer" via the
        // fuzzy second chance and Kölner Phonetik equality.
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = new List<ClientSearchItem>() });
        _fuzzySearchService.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = Guid.NewGuid(), IdNumber = 990003, FirstName = "Petra", Name = "Mayer" } });
        var context = new LLMContext { Message = "Ändere die Nummer von Frau Meier" };

        await _grounder.GroundAsync(context);

        context.EntityGroundingBlock.ShouldNotBeNull();
        context.EntityGroundingBlock!.ShouldContain("Mayer, Petra (#990003)");
    }

    [Test]
    public async Task GroundAsync_ImplausibleHit_LeavesBlockNull()
    {
        var result = new ClientSearchResult
        {
            Items = new List<ClientSearchItem>
            {
                new() { Id = Guid.NewGuid(), IdNumber = 990009, FirstName = "Andreas", LastName = "Zimmermann" }
            }
        };
        _repository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(result);
        var context = new LLMContext { Message = "Ändere die Nummer von Frau Müller" };

        await _grounder.GroundAsync(context);

        context.EntityGroundingBlock.ShouldBeNull();
    }
}
