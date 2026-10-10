// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the shared contract reference resolver: a GUID is taken as the id without touching the
/// repository, a name is resolved against the non-deleted contracts, an unknown, ambiguous or malformed value
/// is a clean error that lists the real contract names (capped), and the strict id parser used by the
/// destructive skill never matches by name and never throws.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Contracts;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ContractReferenceResolverTests
{
    private const string Parameter = "contractId";
    private const ContractNameMatchMode Mode = ContractNameMatchMode.ExactOrUniquePartial;

    private static IContractRepository Repository(IEnumerable<Contract> contracts)
    {
        var repository = Substitute.For<IContractRepository>();
        repository.List().Returns(contracts.ToList());
        return repository;
    }

    [Test]
    public async Task Guid_IsReturnedWithoutListingContracts()
    {
        var repository = Repository([]);
        var id = Guid.NewGuid();

        var (resolved, error) = await ContractReferenceResolver.ResolveIdAsync(
            repository, $" {id} ", Parameter, Mode, CancellationToken.None);

        resolved.ShouldBe(id);
        error.ShouldBeNull();
        await repository.DidNotReceive().List();
    }

    [Test]
    public async Task ExactNameIsCaseInsensitive()
    {
        var contract = new Contract { Id = Guid.NewGuid(), Name = "Vollzeit 160 BE" };

        var (resolved, error) = await ContractReferenceResolver.ResolveIdAsync(
            Repository([contract, new Contract { Id = Guid.NewGuid(), Name = "Teilzeit" }]),
            "VOLLZEIT 160 be", Parameter, Mode, CancellationToken.None);

        resolved.ShouldBe(contract.Id);
        error.ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task BlankReference_IsAnErrorNamingTheParameter(string? reference)
    {
        var (resolved, error) = await ContractReferenceResolver.ResolveIdAsync(
            Repository([]), reference, Parameter, Mode, CancellationToken.None);

        resolved.ShouldBeNull();
        error.ShouldContain(Parameter);
    }

    [Test]
    public async Task UnknownName_ListsAtMostTheCappedNumberOfRealNames()
    {
        var contracts = Enumerable.Range(1, 30)
            .Select(i => new Contract { Id = Guid.NewGuid(), Name = $"Vertrag Nr {i:00} Alpha" })
            .ToList();

        var (resolved, error) = await ContractReferenceResolver.ResolveIdAsync(
            Repository(contracts), "Zebra Gamma", Parameter, Mode, CancellationToken.None);

        resolved.ShouldBeNull();
        error.ShouldContain("No contract found matching");
        error.ShouldContain("Vertrag Nr 01 Alpha");
        error.ShouldNotContain("Vertrag Nr 30 Alpha");
        error.ShouldContain("and 10 more");
    }

    [TestCase("Vollzeit 160 BE")]
    [TestCase("123e4567-e89b-12d3-a456-42661417400Z")]
    [TestCase("not-a-guid")]
    public void ParseStrictId_RejectsNamesAndMalformedIds_WithoutThrowing(string reference)
    {
        var (parsed, error) = ContractReferenceResolver.ParseStrictId(reference, Parameter);

        parsed.ShouldBeNull();
        error.ShouldContain(Parameter);
        error.ShouldContain("does not match by name");
    }

    [Test]
    public void ParseStrictId_AcceptsAGuid()
    {
        var id = Guid.NewGuid();

        var (parsed, error) = ContractReferenceResolver.ParseStrictId(id.ToString(), Parameter);

        parsed.ShouldBe(id);
        error.ShouldBeNull();
    }

    [Test]
    public async Task Typo_IsRejectedInExactOrUniquePartialMode_ButResolvedWithFuzzy()
    {
        var contract = new Contract { Id = Guid.NewGuid(), Name = "Vollzeit 160 BE" };
        var repository = Repository([contract, new Contract { Id = Guid.NewGuid(), Name = "Teilzeit 0 Std BE" }]);

        var (strictId, strictError) = await ContractReferenceResolver.ResolveIdAsync(
            repository, "Volzeit 160 BE", Parameter, ContractNameMatchMode.ExactOrUniquePartial, CancellationToken.None);
        var (fuzzyId, fuzzyError) = await ContractReferenceResolver.ResolveIdAsync(
            repository, "Volzeit 160 BE", Parameter, ContractNameMatchMode.WithFuzzy, CancellationToken.None);

        strictId.ShouldBeNull();
        strictError.ShouldContain("No contract found matching");
        strictError.ShouldContain("Vollzeit 160 BE");
        fuzzyId.ShouldBe(contract.Id);
        fuzzyError.ShouldBeNull();
    }

    [Test]
    public async Task LabelPrefixedQuery_IsNotAnExactOrPartialNameInStrictMode()
    {
        var repository = Repository([new Contract { Id = Guid.NewGuid(), Name = "Teilzeit 0 Std BE" }]);

        var (resolved, error) = await ContractReferenceResolver.ResolveIdAsync(
            repository, "Vertrag Teilzeit 0 Std BE", Parameter, Mode, CancellationToken.None);

        resolved.ShouldBeNull();
        error.ShouldContain("No contract found matching");
    }
}
