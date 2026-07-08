// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContractResolver, driven by the 2026-07-07 skill-usage failure classes: a
/// label-prefixed query ("Vertrag Teilzeit 0 Std BE") resolves to the real contract, a verbatim
/// user sentence that is no contract name at all yields a not-found error that forbids retrying
/// with the same value, an ambiguous partial name lists all candidates instead of silently
/// picking one, a single typo still resolves, and soft-deleted contracts are ignored.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ContractResolverTests
{
    private static readonly Guid FullTime160BeId = Guid.NewGuid();
    private static readonly Guid FullTime180BeId = Guid.NewGuid();
    private static readonly Guid PartTimeBeId = Guid.NewGuid();
    private static readonly Guid FullTime160ZhId = Guid.NewGuid();

    private static List<Contract> Contracts() => new()
    {
        new() { Id = FullTime160BeId, Name = "Vollzeit 160 BE" },
        new() { Id = FullTime180BeId, Name = "Vollzeit 180 BE" },
        new() { Id = PartTimeBeId, Name = "Teilzeit 0 Std BE" },
        new() { Id = FullTime160ZhId, Name = "Vollzeit 160 ZH" }
    };

    [Test]
    public void ExactName_Resolves()
    {
        var (contract, error) = ContractResolver.Resolve(Contracts(), "Teilzeit 0 Std BE");

        Assert.That(error, Is.Null);
        Assert.That(contract!.Id, Is.EqualTo(PartTimeBeId));
    }

    [Test]
    public void LabelPrefixedQuery_ResolvesRealContract()
    {
        var (contract, error) = ContractResolver.Resolve(Contracts(), "Vertrag Teilzeit 0 Std BE");

        Assert.That(error, Is.Null);
        Assert.That(contract!.Id, Is.EqualTo(PartTimeBeId));
    }

    [Test]
    public void VerbatimUserSentence_NotFound_ForbidsRetryAndListsRealContracts()
    {
        var (contract, error) = ContractResolver.Resolve(
            Contracts(), "Keine Kontaktdaten, bitte ohne Adresse anlegen.");

        Assert.That(contract, Is.Null);
        Assert.That(error, Does.Contain("No contract found matching"));
        Assert.That(error, Does.Contain("Do not call this skill again with the same value"));
        Assert.That(error, Does.Contain("Vollzeit 160 BE"));
        Assert.That(error, Does.Contain("do not invent contracts"));
    }

    [Test]
    public void AmbiguousPartialName_ListsAllCandidates_AndDoesNotPickOne()
    {
        var (contract, error) = ContractResolver.Resolve(Contracts(), "Vollzeit");

        Assert.That(contract, Is.Null);
        Assert.That(error, Does.Contain("Multiple contracts match"));
        Assert.That(error, Does.Contain("Vollzeit 160 BE"));
        Assert.That(error, Does.Contain("Vollzeit 180 BE"));
        Assert.That(error, Does.Contain("Vollzeit 160 ZH"));
        Assert.That(error, Does.Contain("do not guess"));
    }

    [Test]
    public void SingleTypo_StillResolves()
    {
        var (contract, error) = ContractResolver.Resolve(Contracts(), "Teilzeit 0 Sdt BE");

        Assert.That(error, Is.Null);
        Assert.That(contract!.Id, Is.EqualTo(PartTimeBeId));
    }

    [Test]
    public void SoftDeletedContracts_AreIgnored()
    {
        var contracts = new List<Contract>
        {
            new() { Id = PartTimeBeId, Name = "Teilzeit 0 Std BE", IsDeleted = true },
            new() { Id = FullTime160BeId, Name = "Vollzeit 160 BE" }
        };

        var (contract, error) = ContractResolver.Resolve(contracts, "Teilzeit 0 Std BE");

        Assert.That(contract, Is.Null);
        Assert.That(error, Does.Contain("No contract found"));
    }

    [Test]
    public void NoContractsAtAll_ReturnsThereAreNoContractsYet()
    {
        var (contract, error) = ContractResolver.Resolve(new List<Contract>(), "Vollzeit");

        Assert.That(contract, Is.Null);
        Assert.That(error, Does.Contain("There are no contracts yet."));
    }
}
