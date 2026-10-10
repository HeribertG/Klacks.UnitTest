// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ContractTemplateComparer: per-field differences, workload-path mismatches, region mismatch as
/// a difference but never a filter, ranking by difference count with name tie-break, the top-N cap and the
/// recommendation texts. The comparer is pure, so every test builds plain contracts and asserts the outcome.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Services.Contracts;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Contracts;

[TestFixture]
public class ContractTemplateComparerTests
{
    private static readonly IReadOnlyDictionary<Guid, string> NoCalendars = new Dictionary<Guid, string>();

    private static ContractTemplateWish Wish(
        PaymentInterval? interval = null,
        decimal? guaranteedHours = null,
        decimal? percent = null,
        decimal? fullTime = null,
        bool? shiftWork = null,
        IReadOnlySet<DayOfWeek>? workdays = null,
        ContractTemplateRegionWish? region = null) =>
        new(interval, guaranteedHours, percent, fullTime, shiftWork, workdays, region);

    private static Contract FixedHours(string name, decimal hours) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        GuaranteedHours = hours,
        MinimumHours = hours,
        MaximumHours = hours,
        FullTime = 180m,
        PaymentInterval = PaymentInterval.Monthly,
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Contract Inherited(string name, decimal? percent) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        GuaranteedHours = null,
        Percent = percent,
        PaymentInterval = PaymentInterval.Monthly,
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void NoWishes_NoDifferences()
    {
        var differences = ContractTemplateComparer.Compare(FixedHours("A", 160m), Wish(), NoCalendars);

        differences.ShouldBeEmpty();
    }

    [Test]
    public void GuaranteedHours_SameValue_NoDifference_OtherValue_OneDifference()
    {
        var template = FixedHours("A", 160m);

        ContractTemplateComparer.Compare(template, Wish(guaranteedHours: 160m), NoCalendars).ShouldBeEmpty();

        var differences = ContractTemplateComparer.Compare(template, Wish(guaranteedHours: 120m), NoCalendars);
        differences.Count.ShouldBe(1);
        differences[0].Field.ShouldBe(ContractFieldNames.GuaranteedHours);
        differences[0].TemplateValue.ShouldBe("160");
        differences[0].WishedValue.ShouldBe("120");
    }

    [Test]
    public void PercentWish_AgainstFixedHoursTemplate_IsOnePathMismatchDifference()
    {
        var differences = ContractTemplateComparer.Compare(FixedHours("A", 160m), Wish(percent: 80m), NoCalendars);

        differences.Count.ShouldBe(1);
        differences[0].Field.ShouldBe(ContractFieldNames.Percent);
        differences[0].TemplateValue.ShouldContain("fixed 160");
    }

    [Test]
    public void GuaranteedHoursWish_AgainstInheritedTemplate_IsOnePathMismatchDifference()
    {
        var differences = ContractTemplateComparer.Compare(Inherited("A", 80m), Wish(guaranteedHours: 120m), NoCalendars);

        differences.Count.ShouldBe(1);
        differences[0].Field.ShouldBe(ContractFieldNames.GuaranteedHours);
        differences[0].TemplateValue.ShouldContain("inherited");
        differences[0].TemplateValue.ShouldContain("80");
    }

    [Test]
    public void Percent_TemplateWithoutPercent_CountsAsFullWorkload()
    {
        var template = Inherited("A", null);

        ContractTemplateComparer.Compare(template, Wish(percent: 100m), NoCalendars).ShouldBeEmpty();
        ContractTemplateComparer.Compare(template, Wish(percent: 80m), NoCalendars).Count.ShouldBe(1);
    }

    [Test]
    public void Percent_SameInheritedValue_NoDifference()
    {
        ContractTemplateComparer.Compare(Inherited("A", 80m), Wish(percent: 80m), NoCalendars).ShouldBeEmpty();
    }

    [Test]
    public void MonthlyTargetHoursTemplate_WithGuaranteedHours_IsTreatedAsInheritedWorkload()
    {
        var template = FixedHours("A", 160m);
        template.PaymentInterval = PaymentInterval.MonthlyTargetHours;
        template.Percent = 50m;

        ContractTemplateComparer.IsInheritedWorkload(template).ShouldBeTrue();
        ContractTemplateComparer.Compare(template, Wish(percent: 50m), NoCalendars).ShouldBeEmpty();
        ContractTemplateComparer.Compare(template, Wish(guaranteedHours: 160m), NoCalendars).Count.ShouldBe(1);
    }

    [Test]
    public void PaymentInterval_Difference_ShowsBothNames()
    {
        var differences = ContractTemplateComparer.Compare(
            FixedHours("A", 160m), Wish(interval: PaymentInterval.Weekly), NoCalendars);

        differences.Count.ShouldBe(1);
        differences[0].Field.ShouldBe(ContractFieldNames.PaymentInterval);
        differences[0].TemplateValue.ShouldBe(nameof(PaymentInterval.Monthly));
        differences[0].WishedValue.ShouldBe(nameof(PaymentInterval.Weekly));
    }

    [Test]
    public void FullTime_Difference_AndNullTemplateValueCountsAsZero()
    {
        var template = FixedHours("A", 160m);
        template.FullTime = null;

        ContractTemplateComparer.Compare(template, Wish(fullTime: 0m), NoCalendars).ShouldBeEmpty();
        ContractTemplateComparer.Compare(template, Wish(fullTime: 180m), NoCalendars).Count.ShouldBe(1);
    }

    [Test]
    public void ShiftWork_TemplateStandard_AgainstExplicitWish_IsDifferenceShownAsStandard()
    {
        var template = FixedHours("A", 160m);
        template.PerformsShiftWork = null;

        var differences = ContractTemplateComparer.Compare(template, Wish(shiftWork: false), NoCalendars);

        differences.Count.ShouldBe(1);
        differences[0].TemplateValue.ShouldBe(ContractTemplateAdvisoryDefaults.StandardValueLabel);
        differences[0].WishedValue.ShouldBe("false");
    }

    [Test]
    public void ShiftWork_SameExplicitValue_NoDifference()
    {
        var template = FixedHours("A", 160m);
        template.PerformsShiftWork = true;

        ContractTemplateComparer.Compare(template, Wish(shiftWork: true), NoCalendars).ShouldBeEmpty();
    }

    [Test]
    public void Workdays_DifferentSet_IsDifferenceWithFormattedLists()
    {
        var template = FixedHours("A", 160m);
        var wished = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Saturday };

        var differences = ContractTemplateComparer.Compare(template, Wish(workdays: wished), NoCalendars);

        differences.Count.ShouldBe(1);
        differences[0].Field.ShouldBe(ContractFieldNames.Workdays);
        differences[0].TemplateValue.ShouldBe("Mon,Tue,Wed,Thu,Fri");
        differences[0].WishedValue.ShouldBe("Mon,Tue,Sat");
    }

    [Test]
    public void Workdays_SameSet_NoDifference()
    {
        var wished = new HashSet<DayOfWeek>
        {
            DayOfWeek.Friday, DayOfWeek.Thursday, DayOfWeek.Wednesday, DayOfWeek.Tuesday, DayOfWeek.Monday
        };

        ContractTemplateComparer.Compare(FixedHours("A", 160m), Wish(workdays: wished), NoCalendars).ShouldBeEmpty();
    }

    [Test]
    public void Region_TemplateCalendarCoversRegion_NoDifference()
    {
        var calendarId = Guid.NewGuid();
        var template = FixedHours("A", 160m);
        template.CalendarSelectionId = calendarId;
        var region = new ContractTemplateRegionWish("ZH", new HashSet<Guid> { calendarId });

        ContractTemplateComparer.Compare(template, Wish(region: region), NoCalendars).ShouldBeEmpty();
    }

    [Test]
    public void Region_TemplateWithOtherOrNoCalendar_IsDifferenceNamingTheTemplateCalendar()
    {
        var otherId = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [otherId] = "Bern" };
        var region = new ContractTemplateRegionWish("ZH", new HashSet<Guid> { Guid.NewGuid() });

        var other = FixedHours("A", 160m);
        other.CalendarSelectionId = otherId;
        var none = FixedHours("B", 160m);

        var otherDifferences = ContractTemplateComparer.Compare(other, Wish(region: region), names);
        otherDifferences.Count.ShouldBe(1);
        otherDifferences[0].TemplateValue.ShouldBe("Bern");
        otherDifferences[0].WishedValue.ShouldBe("ZH");

        ContractTemplateComparer.Compare(none, Wish(region: region), names)[0].TemplateValue
            .ShouldBe(ContractTemplateAdvisoryDefaults.NoOwnCalendarLabel);
    }

    [Test]
    public void Region_WithoutAnyMatchingCalendar_IsStillOnlyADifference_NotAFilter()
    {
        var region = new ContractTemplateRegionWish("XX", new HashSet<Guid>());
        var templates = new[] { FixedHours("A", 160m), FixedHours("B", 160m) };

        var ranked = ContractTemplateComparer.Rank(templates, Wish(region: region), NoCalendars, 3);

        ranked.Count.ShouldBe(2);
        ranked.ShouldAllBe(candidate => candidate.DifferenceCount == 1);
    }

    [Test]
    public void Rank_OrdersByDifferenceCount_ThenByName()
    {
        var closest = FixedHours("Zeta closest", 120m);
        var tieB = FixedHours("beta", 160m);
        var tieA = FixedHours("Alpha", 160m);
        var wish = Wish(guaranteedHours: 120m, interval: PaymentInterval.Monthly);

        var ranked = ContractTemplateComparer.Rank(new[] { tieB, closest, tieA }, wish, NoCalendars, 3);

        ranked.Select(c => c.Name).ShouldBe(new[] { "Zeta closest", "Alpha", "beta" });
        ranked.Select(c => c.DifferenceCount).ShouldBe(new[] { 0, 1, 1 });
    }

    [Test]
    public void Rank_ReturnsOnlyTheTopCandidates()
    {
        var templates = Enumerable.Range(1, 6).Select(i => FixedHours($"Contract {i}", 100m + i)).ToList();

        var ranked = ContractTemplateComparer.Rank(
            templates, Wish(guaranteedHours: 103m), NoCalendars, ContractTemplateAdvisoryDefaults.MaxCandidates);

        ranked.Count.ShouldBe(ContractTemplateAdvisoryDefaults.MaxCandidates);
        ranked[0].Name.ShouldBe("Contract 3");
    }

    [Test]
    public void Rank_IsDeterministic_ForIdenticalInput()
    {
        var templates = Enumerable.Range(1, 5).Select(i => FixedHours($"Same {i % 2}", 160m)).ToList();
        var wish = Wish(guaranteedHours: 100m);

        var first = ContractTemplateComparer.Rank(templates, wish, NoCalendars, 5).Select(c => c.Id).ToList();
        var second = ContractTemplateComparer.Rank(templates.AsEnumerable().Reverse(), wish, NoCalendars, 5)
            .Select(c => c.Id).ToList();

        second.ShouldBe(first);
    }

    [Test]
    public void Rank_Candidate_CarriesKeyValues()
    {
        var calendarId = Guid.NewGuid();
        var template = FixedHours("Vollzeit 160 ZH", 160m);
        template.CalendarSelectionId = calendarId;
        template.PerformsShiftWork = true;

        var candidate = ContractTemplateComparer.Rank(
            new[] { template }, Wish(), new Dictionary<Guid, string> { [calendarId] = "Zürich" }, 3)[0];

        candidate.Id.ShouldBe(template.Id);
        candidate.GuaranteedHours.ShouldBe(160m);
        candidate.PaymentInterval.ShouldBe(nameof(PaymentInterval.Monthly));
        candidate.PerformsShiftWork.ShouldBe(true);
        candidate.Workdays.ShouldBe("Mon,Tue,Wed,Thu,Fri");
        candidate.HolidayCalendarName.ShouldBe("Zürich");
    }

    [Test]
    public void Recommendation_NoContracts_SaysCreateFromScratch()
    {
        var text = ContractTemplateComparer.BuildRecommendation([], 0, Wish(percent: 80m));

        text.ShouldContain("No active contract exists");
        text.ShouldContain("from scratch");
    }

    [Test]
    public void Recommendation_NoWishes_AsksForTheMissingFacts()
    {
        var ranked = ContractTemplateComparer.Rank(new[] { FixedHours("A", 160m) }, Wish(), NoCalendars, 3);

        var text = ContractTemplateComparer.BuildRecommendation(ranked, 1, Wish());

        text.ShouldContain("no wishes were given");
        text.ShouldNotContain("Best match");
    }

    [Test]
    public void Recommendation_PerfectMatch_SaysUnchangedAndWaitsForConfirmation()
    {
        var template = FixedHours("Vollzeit 160", 160m);
        var wish = Wish(guaranteedHours: 160m);
        var ranked = ContractTemplateComparer.Rank(new[] { template }, wish, NoCalendars, 3);

        var text = ContractTemplateComparer.BuildRecommendation(ranked, 1, wish);

        text.ShouldContain("'Vollzeit 160'");
        text.ShouldContain(template.Id.ToString());
        text.ShouldContain("unchanged");
        text.ShouldContain("ask which template to use");
        text.ShouldContain("do not ask a separate");
    }

    [Test]
    public void Recommendation_WithDifferences_NamesCountAndFields()
    {
        var template = FixedHours("Vollzeit 160", 160m);
        var wish = Wish(guaranteedHours: 120m, interval: PaymentInterval.Weekly, fullTime: 180m);
        var ranked = ContractTemplateComparer.Rank(new[] { template }, wish, NoCalendars, 3);

        var text = ContractTemplateComparer.BuildRecommendation(ranked, 1, wish);

        text.ShouldContain("differs in 2 of 3 wished field(s)");
        text.ShouldContain(ContractFieldNames.PaymentInterval);
        text.ShouldContain(ContractFieldNames.GuaranteedHours);
        text.ShouldContain("adapting 2 field(s)");
    }

    private static Contract TeilzeitZero() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Teilzeit 0 Std ZH",
        GuaranteedHours = 0m,
        MinimumHours = 0m,
        MaximumHours = 75m,
        FullTime = 180m,
        PaymentInterval = PaymentInterval.Monthly,
        ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void PercentWish_OnAFixedHoursTemplate_ReportsTheDroppedHoursAndTheResetBandAsImpliedChanges()
    {
        var template = TeilzeitZero();

        var candidate = ContractTemplateComparer.Rank(new[] { template }, Wish(percent: 80m), NoCalendars, 3)[0];

        candidate.DifferenceCount.ShouldBe(1);
        candidate.Differences.Select(d => d.Field).ShouldBe(new[] { ContractFieldNames.Percent });
        candidate.ImpliedChanges.Select(c => (c.Field, c.TemplateValue, c.ResultingValue)).ShouldBe(new[]
        {
            (ContractFieldNames.GuaranteedHours, "0", ContractTemplateAdvisoryDefaults.InheritedWorkloadLabel),
            (ContractFieldNames.MaximumHours, "75", "0")
        });
    }

    [Test]
    public void PercentWish_BandAlreadyZero_ReportsOnlyTheDroppedGuaranteedHours()
    {
        var template = FixedHours("A", 160m);
        template.MinimumHours = 0m;
        template.MaximumHours = 0m;

        var changes = ContractTemplateComparer.ImpliedChanges(template, Wish(percent: 80m));

        changes.Select(c => c.Field).ShouldBe(new[] { ContractFieldNames.GuaranteedHours });
    }

    [Test]
    public void PercentWish_OnAnInheritedTemplate_HasNoGuaranteedHoursToDrop_AndItsBandIsAlreadyZero()
    {
        ContractTemplateComparer.ImpliedChanges(Inherited("A", 80m), Wish(percent: 100m)).ShouldBeEmpty();
    }

    [Test]
    public void GuaranteedHoursWish_CollapsesTheBandAndDropsThePercent()
    {
        var template = FixedHours("A", 160m);
        template.MinimumHours = 120m;
        template.MaximumHours = 200m;
        template.Percent = 90m;

        var changes = ContractTemplateComparer.ImpliedChanges(template, Wish(guaranteedHours: 140m));

        changes.Select(c => (c.Field, c.TemplateValue, c.ResultingValue)).ShouldBe(new[]
        {
            (ContractFieldNames.Percent, "90 %", ContractTemplateAdvisoryDefaults.NoPercentLabel),
            (ContractFieldNames.MinimumHours, "120", "140"),
            (ContractFieldNames.MaximumHours, "200", "140")
        });
    }

    [Test]
    public void GuaranteedHoursWish_SameHoursAsTheTemplate_StillReportsTheCollapsingBand()
    {
        var template = FixedHours("A", 160m);
        template.MinimumHours = 120m;
        template.MaximumHours = 200m;

        var candidate = ContractTemplateComparer.Rank(new[] { template }, Wish(guaranteedHours: 160m), NoCalendars, 3)[0];

        candidate.DifferenceCount.ShouldBe(0);
        candidate.ImpliedChanges.Select(c => c.Field)
            .ShouldBe(new[] { ContractFieldNames.MinimumHours, ContractFieldNames.MaximumHours });
    }

    [Test]
    public void WithoutAWorkloadWish_NoImpliedChangesAreReported()
    {
        ContractTemplateComparer.ImpliedChanges(TeilzeitZero(), Wish(interval: PaymentInterval.Weekly)).ShouldBeEmpty();
    }

    [Test]
    public void ImpliedChanges_AreNamedInTheRecommendation_ButNotCountedAsWishFields()
    {
        var wish = Wish(percent: 80m);
        var ranked = ContractTemplateComparer.Rank(new[] { TeilzeitZero() }, wish, NoCalendars, 3);

        var text = ContractTemplateComparer.BuildRecommendation(ranked, 1, wish);

        text.ShouldContain("differs in 1 of 1 wished field(s): percent");
        text.ShouldContain("implied, not wishes");
        text.ShouldContain("guaranteedHours (0 -> none (inherited from the company-wide value))");
        text.ShouldContain("maximumHours (75 -> 0)");
    }

    [Test]
    public void WishCount_CountsOnlyGivenMembers()
    {
        Wish().WishCount.ShouldBe(0);
        Wish(percent: 80m, shiftWork: false, region: new ContractTemplateRegionWish("ZH", new HashSet<Guid>()))
            .WishCount.ShouldBe(3);
    }
}
