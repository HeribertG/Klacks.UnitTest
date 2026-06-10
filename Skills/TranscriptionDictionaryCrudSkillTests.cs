// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the transcription dictionary skills: list projects all entry fields and
/// filters by language/search, add parses delimited and JSON-array variants, update patches
/// only supplied fields and rejects empty calls, delete reports missing entries.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class TranscriptionDictionaryCrudSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static TranscriptionDictionaryEntry Entry(string term, string? language = null) => new()
    {
        Id = Guid.NewGuid(),
        CorrectTerm = term,
        Category = "product",
        PhoneticVariants = ["klaxi", "klacksi"],
        Description = "Assistant name",
        Language = language
    };

    [Test]
    public async Task List_ReturnsEntries()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTranscriptionDictionaryEntriesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<TranscriptionDictionaryEntry> { Entry("Klacksy", "de"), Entry("Spitex", "de") });
        var skill = new ListTranscriptionDictionaryEntriesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 transcription dictionary entries");
    }

    [Test]
    public async Task List_LanguageAndSearchFilters_ReduceResult()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTranscriptionDictionaryEntriesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<TranscriptionDictionaryEntry> { Entry("Klacksy", "de"), Entry("Klacksy", "fr"), Entry("Spitex", "de") });
        var skill = new ListTranscriptionDictionaryEntriesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["language"] = "DE",
            ["search"] = "klacksy"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("1 transcription dictionary entries");
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTranscriptionDictionaryEntriesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<TranscriptionDictionaryEntry>());
        var skill = new ListTranscriptionDictionaryEntriesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 transcription dictionary entries");
    }

    [Test]
    public async Task Add_WithDelimitedVariants_SendsCommand()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var c = call.Arg<CreateTranscriptionDictionaryEntryCommand>();
                return new TranscriptionDictionaryEntry
                {
                    Id = Guid.NewGuid(),
                    CorrectTerm = c.CorrectTerm,
                    Category = c.Category,
                    PhoneticVariants = c.PhoneticVariants,
                    Description = c.Description,
                    Language = c.Language
                };
            });
        var skill = new AddTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["correctTerm"] = " Klacksy ",
            ["phoneticVariants"] = "klaxi, klacksi; klaksie",
            ["language"] = "DE"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("3 phonetic variant(s)");
        await mediator.Received(1).Send(
            Arg.Is<CreateTranscriptionDictionaryEntryCommand>(c =>
                c.CorrectTerm == "Klacksy" &&
                c.PhoneticVariants.Count == 3 &&
                c.PhoneticVariants.Contains("klaksie") &&
                c.Language == "de" &&
                c.Category == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Add_WithJsonArrayVariants_ParsesArray()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new TranscriptionDictionaryEntry
            {
                Id = Guid.NewGuid(),
                CorrectTerm = call.Arg<CreateTranscriptionDictionaryEntryCommand>().CorrectTerm,
                PhoneticVariants = call.Arg<CreateTranscriptionDictionaryEntryCommand>().PhoneticVariants
            });
        var skill = new AddTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["correctTerm"] = "Spitex",
            ["phoneticVariants"] = """["spitäx", "spi tex"]"""
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<CreateTranscriptionDictionaryEntryCommand>(c =>
                c.PhoneticVariants.Count == 2 &&
                c.PhoneticVariants.Contains("spitäx")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Add_MissingCorrectTerm_ReturnsErrorWithoutSend()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new AddTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["phoneticVariants"] = "klaxi"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("correctTerm");
        await mediator.DidNotReceive().Send(
            Arg.Any<CreateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_PatchesSuppliedFields()
    {
        var entryId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns(new TranscriptionDictionaryEntry { Id = entryId, CorrectTerm = "Klacksy" });
        var skill = new UpdateTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = entryId.ToString(),
            ["phoneticVariants"] = "klaxi, klacksi",
            ["category"] = ""
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<UpdateTranscriptionDictionaryEntryCommand>(c =>
                c.Id == entryId &&
                c.CorrectTerm == null &&
                c.PhoneticVariants != null &&
                c.PhoneticVariants.Count == 2 &&
                c.Category == "" &&
                c.Description == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_EntryNotFound_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns((TranscriptionDictionaryEntry?)null);
        var skill = new UpdateTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = Guid.NewGuid().ToString(),
            ["correctTerm"] = "Klacksy"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task Update_NoFieldsSupplied_ReturnsErrorWithoutSend()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing to update");
        await mediator.DidNotReceive().Send(
            Arg.Any<UpdateTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ExistingEntry_Succeeds()
    {
        var entryId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new DeleteTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = entryId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteTranscriptionDictionaryEntryCommand>(c => c.Id == entryId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_MissingEntry_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var skill = new DeleteTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task Delete_InvalidId_ReturnsErrorWithoutSend()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new DeleteTranscriptionDictionaryEntrySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["entryId"] = "not-a-guid"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("entryId");
        await mediator.DidNotReceive().Send(
            Arg.Any<DeleteTranscriptionDictionaryEntryCommand>(), Arg.Any<CancellationToken>());
    }
}
