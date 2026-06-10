// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the transcription dictionary command/query handlers: create persists and
/// invalidates the dictionary cache, update patches only supplied fields and clears
/// empty-string fields, delete returns false for missing entries without cache invalidation.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TranscriptionDictionaryHandlerTests
{
    private ITranscriptionDictionaryRepository _repository = null!;
    private IDictionaryService _dictionaryService = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ITranscriptionDictionaryRepository>();
        _dictionaryService = Substitute.For<IDictionaryService>();
    }

    private static TranscriptionDictionaryEntry Existing(Guid id) => new()
    {
        Id = id,
        CorrectTerm = "Klacksy",
        Category = "product",
        PhoneticVariants = ["klaxi"],
        Description = "Assistant name",
        Language = "de"
    };

    [Test]
    public async Task GetAll_ReturnsRepositoryEntries()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([Existing(Guid.NewGuid()), Existing(Guid.NewGuid())]);
        var handler = new GetTranscriptionDictionaryEntriesQueryHandler(_repository);

        var result = await handler.Handle(new GetTranscriptionDictionaryEntriesQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task Create_PersistsEntry_AndInvalidatesCache()
    {
        var handler = new CreateTranscriptionDictionaryEntryCommandHandler(_repository, _dictionaryService);

        var created = await handler.Handle(
            new CreateTranscriptionDictionaryEntryCommand
            {
                CorrectTerm = "Spitex",
                PhoneticVariants = ["spitäx"],
                Language = "de"
            },
            CancellationToken.None);

        created.Id.ShouldNotBe(Guid.Empty);
        created.CorrectTerm.ShouldBe("Spitex");
        await _repository.Received(1).AddAsync(
            Arg.Is<TranscriptionDictionaryEntry>(e => e.CorrectTerm == "Spitex" && e.PhoneticVariants.Count == 1),
            Arg.Any<CancellationToken>());
        _dictionaryService.Received(1).InvalidateCache();
    }

    [Test]
    public async Task Update_PatchesOnlySuppliedFields_AndClearsEmptyStrings()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Existing(id));
        var handler = new UpdateTranscriptionDictionaryEntryCommandHandler(_repository, _dictionaryService);

        var updated = await handler.Handle(
            new UpdateTranscriptionDictionaryEntryCommand
            {
                Id = id,
                PhoneticVariants = ["klaxi", "klacksi"],
                Category = string.Empty
            },
            CancellationToken.None);

        updated.ShouldNotBeNull();
        updated!.CorrectTerm.ShouldBe("Klacksy");
        updated.PhoneticVariants.Count.ShouldBe(2);
        updated.Category.ShouldBeNull();
        updated.Description.ShouldBe("Assistant name");
        await _repository.Received(1).UpdateAsync(
            Arg.Is<TranscriptionDictionaryEntry>(e => e.Id == id),
            Arg.Any<CancellationToken>());
        _dictionaryService.Received(1).InvalidateCache();
    }

    [Test]
    public async Task Update_MissingEntry_ReturnsNull_WithoutCacheInvalidation()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TranscriptionDictionaryEntry?)null);
        var handler = new UpdateTranscriptionDictionaryEntryCommandHandler(_repository, _dictionaryService);

        var updated = await handler.Handle(
            new UpdateTranscriptionDictionaryEntryCommand { Id = Guid.NewGuid(), CorrectTerm = "X" },
            CancellationToken.None);

        updated.ShouldBeNull();
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<TranscriptionDictionaryEntry>(), Arg.Any<CancellationToken>());
        _dictionaryService.DidNotReceive().InvalidateCache();
    }

    [Test]
    public async Task Delete_ExistingEntry_ReturnsTrue_AndInvalidatesCache()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Existing(id));
        var handler = new DeleteTranscriptionDictionaryEntryCommandHandler(_repository, _dictionaryService);

        var deleted = await handler.Handle(
            new DeleteTranscriptionDictionaryEntryCommand { Id = id },
            CancellationToken.None);

        deleted.ShouldBeTrue();
        await _repository.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        _dictionaryService.Received(1).InvalidateCache();
    }

    [Test]
    public async Task Delete_MissingEntry_ReturnsFalse_WithoutCacheInvalidation()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((TranscriptionDictionaryEntry?)null);
        var handler = new DeleteTranscriptionDictionaryEntryCommandHandler(_repository, _dictionaryService);

        var deleted = await handler.Handle(
            new DeleteTranscriptionDictionaryEntryCommand { Id = Guid.NewGuid() },
            CancellationToken.None);

        deleted.ShouldBeFalse();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _dictionaryService.DidNotReceive().InvalidateCache();
    }
}
