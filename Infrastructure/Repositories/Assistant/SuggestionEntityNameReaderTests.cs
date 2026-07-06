// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SuggestionEntityNameReader — verifies it maps the "contractName"/"groupName"
/// recipe slots to the real, non-deleted entity names from the corresponding repository, and
/// returns null (meaning "do not filter") for any other slot it does not know how to ground.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Repositories.Assistant;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SuggestionEntityNameReaderTests
{
    private IContractRepository _contractRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private SuggestionEntityNameReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _contractRepository = Substitute.For<IContractRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _reader = new SuggestionEntityNameReader(_contractRepository, _groupRepository);
    }

    [Test]
    public async Task GetRealNamesForSlotAsync_ContractSlot_ReturnsNonDeletedContractNames()
    {
        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Name = "Vollzeit 160 BE", IsDeleted = false },
            new() { Name = "Alter Vertrag", IsDeleted = true },
        });

        var result = await _reader.GetRealNamesForSlotAsync("contractName");

        result.ShouldBe(["Vollzeit 160 BE"]);
    }

    [Test]
    public async Task GetRealNamesForSlotAsync_GroupSlot_ReturnsNonDeletedGroupNames()
    {
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Name = "Bern", IsDeleted = false },
            new() { Name = "Alte Gruppe", IsDeleted = true },
        });

        var result = await _reader.GetRealNamesForSlotAsync("groupName");

        result.ShouldBe(["Bern"]);
    }

    [Test]
    public async Task GetRealNamesForSlotAsync_UnknownSlot_ReturnsNull()
    {
        var result = await _reader.GetRealNamesForSlotAsync("employeeName");

        result.ShouldBeNull();
    }
}
