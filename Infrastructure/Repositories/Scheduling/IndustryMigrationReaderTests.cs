// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for IndustryMigrationReader: which contracts land on the migration list after an
/// ACTIVE_INDUSTRIES switch (only those bound to a rule of a now-inactive industry, counted by their
/// active client links) and which target rule is proposed for them. The proposal compares the
/// "region-setup:industryProfiles:{industry}:..." import key tail, so the same preset of a now-active
/// industry is offered - but only when exactly one active industry provides it.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Scheduling;

[TestFixture]
public class IndustryMigrationReaderTests
{
    private const string StandardShiftTail = "rule:standard-shift";
    private const string NightShiftTail = "rule:night-shift";

    private DataBaseContext _context = null!;
    private IActiveIndustriesProvider _activeIndustriesProvider = null!;
    private IndustryMigrationReader _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _activeIndustriesProvider = Substitute.For<IActiveIndustriesProvider>();
        _sut = new IndustryMigrationReader(_context, _activeIndustriesProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetContractsOnInactiveIndustries_SettingMissing_ReturnsEmpty()
    {
        ActiveIndustriesAre(null);
        var rule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        AddContract("Contract A", rule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_RuleOfInactiveIndustry_IsReportedWithActiveClientCount()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var rule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        var contract = AddContract("Contract A", rule.Id);
        AddClientLink(contract.Id, isActive: true);
        AddClientLink(contract.Id, isActive: true);
        AddClientLink(contract.Id, isActive: false);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.ContractId.ShouldBe(contract.Id);
        candidate.ContractName.ShouldBe("Contract A");
        candidate.SchedulingRuleId.ShouldBe(rule.Id);
        candidate.SchedulingRuleName.ShouldBe("Security day");
        candidate.Industry.ShouldBe(IndustrySlugs.Security);
        candidate.AffectedClientCount.ShouldBe(2);
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_RuleOfActiveIndustry_IsNotReported()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var rule = AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddContract("Contract A", rule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_CustomerOwnedRuleWithoutIndustry_IsNeverReported()
    {
        ActiveIndustriesAre(Array.Empty<string>());
        var rule = AddRule("Own rule", string.Empty, string.Empty);
        AddContract("Contract A", rule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_ContractWithoutRule_IsNotReported()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        AddContract("Contract without rule", schedulingRuleId: null);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_DeletedContract_IsNotReported()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var rule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        var contract = AddContract("Contract A", rule.Id);
        await _context.SaveChangesAsync();
        contract.IsDeleted = true;
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_SamePresetUnderTheActiveIndustry_IsSuggested()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var oldRule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        var newRule = AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddPresetRule("Healthcare night", IndustrySlugs.Healthcare, NightShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBe(newRule.Id);
        candidate.SuggestedRuleName.ShouldBe("Healthcare day");
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_ActiveIndustryLacksThePreset_SuggestsNothing()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var oldRule = AddPresetRule("Security night", IndustrySlugs.Security, NightShiftTail);
        AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBeNull();
        candidate.SuggestedRuleName.ShouldBeNull();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_TwoActiveIndustriesOfferThePreset_SuggestsNothing()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare, IndustrySlugs.Homecare });
        var oldRule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddPresetRule("Homecare day", IndustrySlugs.Homecare, StandardShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBeNull();
        candidate.SuggestedRuleName.ShouldBeNull();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_CustomMarkerLeavesNoActiveIndustry_SuggestsNothing()
    {
        ActiveIndustriesAre(Array.Empty<string>());
        var oldRule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBeNull();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_AssignedRuleWithoutImportKey_SuggestsNothing()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var oldRule = AddRule("Hand-tagged security rule", IndustrySlugs.Security, string.Empty);
        AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBeNull();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_DeletedEquivalentRule_IsNotSuggested()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var oldRule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        var newRule = AddPresetRule("Healthcare day", IndustrySlugs.Healthcare, StandardShiftTail);
        AddContract("Contract A", oldRule.Id);
        await _context.SaveChangesAsync();
        newRule.IsDeleted = true;
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        var candidate = result.ShouldHaveSingleItem();
        candidate.SuggestedRuleId.ShouldBeNull();
    }

    [Test]
    public async Task GetContractsOnInactiveIndustries_SeveralCandidates_AreOrderedByContractName()
    {
        ActiveIndustriesAre(new[] { IndustrySlugs.Healthcare });
        var rule = AddPresetRule("Security day", IndustrySlugs.Security, StandardShiftTail);
        AddContract("Zeta contract", rule.Id);
        AddContract("Alpha contract", rule.Id);
        await _context.SaveChangesAsync();

        var result = await _sut.GetContractsOnInactiveIndustriesAsync();

        result.Select(c => c.ContractName).ShouldBe(new[] { "Alpha contract", "Zeta contract" });
    }

    private void ActiveIndustriesAre(IReadOnlyCollection<string>? slugs)
    {
        _activeIndustriesProvider.GetActiveIndustrySlugsAsync().Returns(slugs);
    }

    private SchedulingRule AddPresetRule(string name, string industry, string keyTail)
    {
        var importSourceKey = RegionSetupImportKeys.IndustryProfilesPrefix
                              + industry
                              + RegionSetupImportKeys.SegmentSeparator
                              + keyTail;
        return AddRule(name, industry, importSourceKey);
    }

    private SchedulingRule AddRule(string name, string industry, string importSourceKey)
    {
        var rule = new SchedulingRule
        {
            Id = Guid.NewGuid(),
            Name = name,
            Industry = industry,
            ImportSourceKey = importSourceKey
        };
        _context.SchedulingRules.Add(rule);
        return rule;
    }

    private Contract AddContract(string name, Guid? schedulingRuleId)
    {
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = name,
            ValidFrom = new DateTime(2026, 1, 1),
            SchedulingRuleId = schedulingRuleId
        };
        _context.Contract.Add(contract);
        return contract;
    }

    private void AddClientLink(Guid contractId, bool isActive)
    {
        _context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ContractId = contractId,
            FromDate = new DateOnly(2026, 1, 1),
            IsActive = isActive
        });
    }
}
