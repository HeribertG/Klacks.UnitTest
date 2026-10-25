// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for FillGroupByCriteriaSkill: unknown group / ambiguous contract or qualification names
/// are rejected with helpful errors, the preview path sends Apply=false, city/zipPrefix/qualification
/// filters are resolved and forwarded to the command, and an apply flag arriving as a JsonElement
/// (the real tool-call shape) is correctly read as true. A random selection is seeded from the request, so preview
/// and apply match.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class FillGroupByCriteriaSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IContractRepository _contractRepository = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
    private FillGroupByCriteriaSkill _skill = null!;

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid FirstAidQualificationId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _contractRepository, _mediator, _companyClock,
            TestAddressLocationResolver.SwissAndGerman());

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = BernGroupId, Name = "Bern" },
            new() { Id = Guid.NewGuid(), Name = "Zürich" }
        });

        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = Guid.NewGuid(), Name = "180 BE" }
        });

        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification>
            {
                new()
                {
                    Id = FirstAidQualificationId,
                    Name = new MultiLanguage { De = "Erste Hilfe", En = "First Aid", Fr = "Premiers secours", It = "Primo soccorso" }
                }
            }.AsEnumerable());

        _mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StateResource>
            {
                new() { Abbreviation = "BE", Name = new MultiLanguage { De = "Bern", En = "Bern", Fr = "Berne", It = "Berna" } },
                new() { Abbreviation = "ZH", Name = new MultiLanguage { De = "Zürich", En = "Zurich", Fr = "Zurich", It = "Zurigo" } }
            }.AsEnumerable());

        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var cmd = ci.Arg<FillGroupByCriteriaCommand>();
                return Task.FromResult(new FillGroupByCriteriaResult(
                    cmd.Apply, cmd.GroupName, 1, cmd.Apply ? 1 : 0, cmd.Apply ? 1 : 0, 0,
                    new List<ClientSearchItem> { new() { Id = Guid.NewGuid(), FirstName = "Max", LastName = "Müller" } }));
            });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenGroupNameIsHallucinated()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Administration" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Bern"));
        Assert.That(result.Message, Does.Contain("Zürich"));
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenContractNameIsAmbiguous()
    {
        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = Guid.NewGuid(), Name = "180 BE" },
            new() { Id = Guid.NewGuid(), Name = "180 BE night" }
        });

        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["contractName"] = "180"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Multiple contracts"));
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_ListingRealQualifications_WhenQualificationNameIsHallucinated()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["qualificationName"] = "Forklift"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No qualification found matching 'Forklift'"));
        Assert.That(result.Message, Does.Contain("Erste Hilfe"));
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenQualificationNameIsAmbiguous()
    {
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification>
            {
                new() { Id = FirstAidQualificationId, Name = new MultiLanguage { De = "Erste Hilfe" } },
                new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Erste Hilfe Plus" } }
            }.AsEnumerable());

        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["qualificationName"] = "Erste Hilfe"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Multiple qualifications"));
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolvesQualificationName_AcrossAllCoreLanguages()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["qualificationName"] = "Premiers secours"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => c.QualificationId == FirstAidQualificationId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_ResolvesQualification_AndForwardsCityAndZipPrefix_ToCommand()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["city"] = " Bern ",
            ["zipPrefix"] = "30",
            ["qualificationName"] = "First Aid"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c =>
                c.City == "Bern" && c.ZipPrefix == "30" && c.QualificationId == FirstAidQualificationId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_SendsApplyFalse_AndResolvesContract()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["canton"] = "BE",
            ["contractName"] = "180 BE"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c =>
                c.GroupId == BernGroupId && c.Canton == "BE" && c.ContractId != null && !c.Apply),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolvesCantonName_ToCode_BeforeSearching()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["canton"] = "Bern"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => c.Canton == "BE"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_AsJsonElement_IsReadAsTrue()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["apply"] = JsonSerializer.SerializeToElement(true),
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Added"));
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => c.Apply),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WithoutValidFrom_AsksForStartDate_AndDoesNotPersist()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["apply"] = JsonSerializer.SerializeToElement(true)
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_WithoutValidFrom_AsksForStartDate_InTheSameTurn()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        Assert.That(result.Message, Does.Contain("validFrom"));
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => !c.Apply),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RandomPreviewAndApply_WithTheSameParameters_UseTheSameSeed()
    {
        var preview = RandomParameters(count: 10, apply: false);
        var apply = RandomParameters(count: 10, apply: true);

        var previewResult = await _skill.ExecuteAsync(Ctx(UserId), preview);
        var applyResult = await _skill.ExecuteAsync(Ctx(UserId), apply);

        Assert.That(previewResult.Success, Is.True);
        Assert.That(applyResult.Success, Is.True);
        var seeds = SentCommands().Select(c => c.RandomSeed).ToList();
        Assert.That(seeds, Has.Count.EqualTo(2));
        Assert.That(seeds[0], Is.Not.Null);
        Assert.That(seeds[1], Is.EqualTo(seeds[0]));
        Assert.That(previewResult.Message, Does.Contain("random"));
    }

    [Test]
    public async Task RandomSelection_WithAnotherCount_UsesAnotherSeed()
    {
        await _skill.ExecuteAsync(Ctx(UserId), RandomParameters(count: 10, apply: false));
        await _skill.ExecuteAsync(Ctx(UserId), RandomParameters(count: 5, apply: false));

        var seeds = SentCommands().Select(c => c.RandomSeed).ToList();
        Assert.That(seeds[1], Is.Not.EqualTo(seeds[0]));
    }

    [Test]
    public async Task WithoutRandom_NoSeedIsSent()
    {
        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern" });

        Assert.That(SentCommands().Single().RandomSeed, Is.Null);
    }

    private static readonly Guid UserId = Guid.NewGuid();

    private static Dictionary<string, object> RandomParameters(int count, bool apply) => new()
    {
        ["groupName"] = "Bern",
        ["canton"] = "BE",
        ["count"] = JsonSerializer.SerializeToElement(count),
        ["random"] = JsonSerializer.SerializeToElement(true),
        ["validFrom"] = "2026-07-01",
        ["apply"] = JsonSerializer.SerializeToElement(apply)
    };

    private static SkillExecutionContext Ctx(Guid userId) => new()
    {
        UserId = userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    private List<FillGroupByCriteriaCommand> SentCommands() =>
        _mediator.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<FillGroupByCriteriaCommand>()
            .ToList();
}
