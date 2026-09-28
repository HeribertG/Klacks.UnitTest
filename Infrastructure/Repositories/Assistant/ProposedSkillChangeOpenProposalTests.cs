// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins which proposals stop the optimizer from opening another one for the same skill. A gate-passed
/// proposal waits for its export, and an exported one waits for the release that reseeds the description -
/// until then the live description is still the one the proposal was written against, and a second proposal
/// would be a duplicate. The Npgsql half proves the subquery translates; InMemory proves nothing about SQL.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Npgsql;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ProposedSkillChangeOpenProposalTests
{
    private const string Before = "Lists everything about clients.";
    private const string After = "Lists the contract data of one client.";
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";
    private const string TranslationFailureMarker = "could not be translated";

    private DbContextOptions<DataBaseContext> _options = null!;
    private Guid _skillId;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _skillId = Guid.NewGuid();
    }

    private DataBaseContext CreateContext() => new(_options, Substitute.For<IHttpContextAccessor>());

    private async Task GivenSkillAsync(string description)
    {
        await using var context = CreateContext();
        context.AgentSkills.Add(new AgentSkill { Id = _skillId, AgentId = Guid.NewGuid(), Name = "list_clients", Description = description });
        await context.SaveChangesAsync();
    }

    private async Task GivenProposalAsync(string status, string field = ProposedChangeFields.Description)
    {
        await new ProposedSkillChangeRepository(CreateContext()).AddAsync(new ProposedSkillChange
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            SkillId = _skillId,
            SkillName = "list_clients",
            Field = field,
            ValueBefore = Before,
            ValueAfter = After,
            Status = status
        });
    }

    private Task<bool> HasOpenAsync(string field = ProposedChangeFields.Description) =>
        new ProposedSkillChangeRepository(CreateContext())
            .HasOpenProposalForSkillAsync(_skillId, field);

    [Test]
    public async Task AGatePassedProposal_IsOpen()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.GatePassed);

        (await HasOpenAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task AnExportedProposal_IsOpenWhileTheLiveDescriptionIsStillTheOldOne()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.Exported);

        (await HasOpenAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task AnExportedProposal_IsClosedOnceTheReleaseReseededTheDescription()
    {
        await GivenSkillAsync(After);
        await GivenProposalAsync(ProposedChangeStatuses.Exported);

        (await HasOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task ARejectedProposal_IsNotOpen()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.Rejected);

        (await HasOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task AnAppliedAutoProposal_IsOpenWhileTheLiveDescriptionIsStillTheAppliedOne()
    {
        await GivenSkillAsync(After);
        await GivenProposalAsync(ProposedChangeStatuses.AppliedAuto);

        (await HasOpenAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task AnAppliedAutoProposal_IsClosedOnceASeedUpdateReplacedTheDescription()
    {
        const string ReseededByNewSeedVersion = "Lists the contract data of one client, grouped by branch.";
        await GivenSkillAsync(ReseededByNewSeedVersion);
        await GivenProposalAsync(ProposedChangeStatuses.AppliedAuto);

        (await HasOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task AGatePassedProposal_IsClosedOnceASeedUpdateReplacedTheDescriptionItWasMeasuredAgainst()
    {
        const string ReseededByNewSeedVersion = "Lists the contract data of one client, grouped by branch.";
        await GivenSkillAsync(ReseededByNewSeedVersion);
        await GivenProposalAsync(ProposedChangeStatuses.GatePassed);

        (await HasOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task AnAppliedAutoProposal_OfANonDescriptionField_IsOpenRegardlessOfTheSkillsDescription()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.AppliedAuto, ProposedChangeFields.RecipeTriggerNarrowing);

        (await HasOpenAsync(ProposedChangeFields.RecipeTriggerNarrowing)).ShouldBeTrue();
    }

    [Test]
    public async Task AGatePassedProposal_OfANonDescriptionField_IsOpenRegardlessOfTheSkillsDescription()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.GatePassed, ProposedChangeFields.RecipeTriggerNarrowing);

        (await HasOpenAsync(ProposedChangeFields.RecipeTriggerNarrowing)).ShouldBeTrue();
    }

    [Test]
    public async Task AnExportedProposal_OfANonDescriptionField_IsNotOpen()
    {
        await GivenSkillAsync(Before);
        await GivenProposalAsync(ProposedChangeStatuses.Exported, ProposedChangeFields.RecipeTriggerNarrowing);

        (await HasOpenAsync(ProposedChangeFields.RecipeTriggerNarrowing)).ShouldBeFalse();
    }

    [Test]
    public async Task TheOpenProposalQuery_TranslatesToSqlOnNpgsql()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var exception = await Should.ThrowAsync<Exception>(() =>
            new ProposedSkillChangeRepository(context).HasOpenProposalForSkillAsync(_skillId, ProposedChangeFields.Description));

        var message = exception.ToString();
        message.ShouldNotContain(TranslationFailureMarker);
        message.ShouldContain(nameof(NpgsqlException));
    }
}
