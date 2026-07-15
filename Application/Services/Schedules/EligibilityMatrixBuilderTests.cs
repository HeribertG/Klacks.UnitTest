// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public sealed class EligibilityMatrixBuilderTests
{
    private static readonly Guid Agent = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Shift = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Qual = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateOnly Date = new(2026, 6, 10);

    private IClientQualificationRepository _clientRepo = null!;
    private IShiftRequiredQualificationRepository _shiftRepo = null!;
    private ISettingsReader _settingsReader = null!;
    private EligibilityMatrixBuilder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepo = Substitute.For<IClientQualificationRepository>();
        _shiftRepo = Substitute.For<IShiftRequiredQualificationRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _sut = new EligibilityMatrixBuilder(_clientRepo, _shiftRepo, _settingsReader);
    }

    private void GivenShiftRequires(bool mandatory, QualificationLevel minLevel)
    {
        _shiftRepo.GetByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftRequiredQualification>
            {
                new()
                {
                    ShiftId = Shift,
                    QualificationId = Qual,
                    IsMandatory = mandatory,
                    MinLevel = minLevel,
                    Qualification = new Qualification { Name = new MultiLanguage { De = "Pflege" }, Emoji = "🩺" },
                    Shift = new Klacks.Api.Domain.Models.Schedules.Shift { Name = "Care", Abbreviation = "C" },
                },
            });
    }

    private void GivenClientHolds(params ClientQualification[] held)
    {
        _clientRepo.GetByClientIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(held.ToList());
    }

    private async Task<EligibilityMatrix> Build()
        => await _sut.BuildAsync(new[] { Agent }, new[] { new EligibilitySlot(Shift, Date) });

    private async Task<EligibilityMatrix> Build(
        IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> preExistingAssignments)
        => await _sut.BuildAsync(new[] { Agent }, new[] { new EligibilitySlot(Shift, Date) }, preExistingAssignments);

    [Test]
    public async Task Missing_MandatoryQualificationNotHeld_IsIneligible()
    {
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds();

        var matrix = await Build();

        matrix.Ineligible.ShouldContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)].ShouldContain(g => g.Reason == QualificationGapReason.Missing);
    }

    [Test]
    public async Task Eligible_HeldAtRequiredLevelInWindow_NotIneligible()
    {
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Proficient });

        var matrix = await Build();

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
    }

    [Test]
    public async Task InsufficientLevel_HeldBelowMinLevel_IsWarningNotBlocking()
    {
        // Severity steuert Veto: a too-low level is a Warning, so the agent stays assignable
        // (not in Ineligible) but is reported.
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Basic });

        var matrix = await Build();

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Reason == QualificationGapReason.InsufficientLevel && g.Severity == QualificationGapSeverity.Warning);
    }

    [Test]
    public async Task Expired_HeldButOutsideValidityWindow_IsWarningNotBlocking()
    {
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification
        {
            ClientId = Agent,
            QualificationId = Qual,
            Level = QualificationLevel.Expert,
            ValidUntil = Date.AddDays(-1),
        });

        var matrix = await Build();

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Warning);
    }

    [Test]
    public async Task NonMandatory_RequirementMissing_IsWarningNotBlocking()
    {
        GivenShiftRequires(mandatory: false, QualificationLevel.Expert);
        GivenClientHolds();

        var matrix = await Build();

        matrix.Ineligible.ShouldBeEmpty();
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Severity == QualificationGapSeverity.Warning);
    }

    [Test]
    public async Task Expired_WithSettingOff_StaysWarningNotBlocking()
    {
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Expert, ValidUntil = Date.AddDays(-1) });

        var matrix = await Build();

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
    }

    [Test]
    public async Task Expired_WithSettingOn_BecomesBlocking()
    {
        _settingsReader.GetSetting(SettingKeys.QualificationExpiredMandatoryBlocks)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiredMandatoryBlocks, Value = "true" });
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Expert, ValidUntil = Date.AddDays(-1) });

        var matrix = await Build();

        matrix.Ineligible.ShouldContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Error);
    }

    [Test]
    public async Task InsufficientLevel_WithSettingOn_StaysWarningNotBlocking()
    {
        // The opt-in setting only escalates Expired; a too-low held level is unaffected.
        _settingsReader.GetSetting(SettingKeys.QualificationExpiredMandatoryBlocks)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiredMandatoryBlocks, Value = "true" });
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Basic });

        var matrix = await Build();

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
    }

    [Test]
    public async Task Expired_WithSettingOn_PreExistingIncumbent_StaysWarningNotBlocking()
    {
        // Regression: a non-locked assignment that already existed BEFORE this evaluation must not be
        // retroactively vetoed when QUALIFICATION_EXPIRED_MANDATORY_BLOCKS flips on — Pre-Commit-Diff-Prinzip.
        _settingsReader.GetSetting(SettingKeys.QualificationExpiredMandatoryBlocks)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiredMandatoryBlocks, Value = "true" });
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Expert, ValidUntil = Date.AddDays(-1) });

        var incumbents = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)> { (Agent.ToString(), Shift, Date) };
        var matrix = await Build(incumbents);

        matrix.Ineligible.ShouldNotContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Warning);
    }

    [Test]
    public async Task Expired_WithSettingOn_OtherAgentOnSameSlot_StaysBlocking()
    {
        // The incumbent protection is scoped to the specific (agent, shift, date) triple: a different
        // agent newly considered for the same slot is still gated by the flag as usual.
        _settingsReader.GetSetting(SettingKeys.QualificationExpiredMandatoryBlocks)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiredMandatoryBlocks, Value = "true" });
        GivenShiftRequires(mandatory: true, QualificationLevel.Proficient);
        GivenClientHolds(new ClientQualification { ClientId = Agent, QualificationId = Qual, Level = QualificationLevel.Expert, ValidUntil = Date.AddDays(-1) });

        var otherAgent = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var incumbents = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)> { (otherAgent.ToString(), Shift, Date) };
        var matrix = await Build(incumbents);

        matrix.Ineligible.ShouldContain((Agent.ToString(), Shift, Date));
        matrix.Gaps[(Agent.ToString(), Shift, Date)]
            .ShouldContain(g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Error);
    }

    [Test]
    public async Task NoRequiredQualifications_EmptyMatrix()
    {
        _shiftRepo.GetByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftRequiredQualification>());
        GivenClientHolds();

        var matrix = await Build();

        matrix.Ineligible.ShouldBeEmpty();
    }
}
