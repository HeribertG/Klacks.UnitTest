// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for UpdateNavigationTargetSynonymsCommandHandler: verifies DB persistence via repository
/// and cache invalidation, replacing the old file-based approach.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Klacksy;

using Klacks.Api.Application.Handlers.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Klacksy.Models;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class UpdateNavigationTargetSynonymsCommandHandlerTests
{
    private INavigationTargetCacheService _cache = null!;
    private INavigationTargetSynonymRepository _synonymRepository = null!;
    private UpdateNavigationTargetSynonymsCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<INavigationTargetCacheService>();
        _synonymRepository = Substitute.For<INavigationTargetSynonymRepository>();
        _handler = new UpdateNavigationTargetSynonymsCommandHandler(_cache, _synonymRepository);
    }

    [Test]
    public async Task Handle_calls_repository_and_invalidates_cache()
    {
        var target = new NavigationTarget { TargetId = "absence", Route = "/absence", LabelKey = "nav.absence" };
        _cache.GetById("absence").Returns(target);

        var command = new UpdateNavigationTargetSynonymsCommand(
            "absence", "de", new[] { "abwesenheit", "fehlzeit" }, "approved");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBeTrue();

        await _synonymRepository.Received(1).ReplaceForTargetLanguageAsync(
            "absence", "de", Arg.Is<IEnumerable<string>>(k => k.SequenceEqual(new[] { "abwesenheit", "fehlzeit" })),
            SynonymSources.User, Arg.Any<CancellationToken>());

        _cache.Received(1).Invalidate();
    }

    [Test]
    public async Task Handle_throws_when_target_not_found_in_cache()
    {
        _cache.GetById("nonexistent").Returns((NavigationTarget?)null);

        var command = new UpdateNavigationTargetSynonymsCommand("nonexistent", "en", new[] { "test" }, "generated");

        Should.Throw<KeyNotFoundException>(() => _handler.Handle(command, CancellationToken.None));

        await _synonymRepository.DidNotReceive().ReplaceForTargetLanguageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_works_for_plugin_locale()
    {
        var target = new NavigationTarget { TargetId = "absence", Route = "/absence", LabelKey = "nav.absence" };
        _cache.GetById("absence").Returns(target);

        var command = new UpdateNavigationTargetSynonymsCommand(
            "absence", "ar", new[] { "تفاصيل الغياب" }, "generated");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldBeTrue();
        await _synonymRepository.Received(1).ReplaceForTargetLanguageAsync(
            "absence", "ar", Arg.Any<IEnumerable<string>>(), SynonymSources.User, Arg.Any<CancellationToken>());
    }
}
