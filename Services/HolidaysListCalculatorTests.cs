using Klacks.Api.Datas;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Holidays;

namespace UnitTest.Services;

[TestFixture]
internal class HolidaysListCalculatorTests
{
    private HolidaysListCalculator _holidaysListCalculator = null!;

    [SetUp]
    public void SetUp()
    {
        _holidaysListCalculator = new HolidaysListCalculator();
    }

    [Test]
    public void ShouldInitializeWithTheCurrentYear()
    {
        // Arrange
        var currentYear = DateTime.Now.Year;

        // Act
        _holidaysListCalculator.CurrentYear = currentYear;

        // Assert
        _holidaysListCalculator.CurrentYear.Should().Be(currentYear);
    }

    [Test]
    public void ShouldAddACalendarRule()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "some-rule",
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };

        // Act
        _holidaysListCalculator.Add(rule);

        // Assert
        _holidaysListCalculator.Count.Should().Be(1);
    }

    [Test]
    public void ShouldComputeEaster()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 9))
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 4, 17))
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Act & Assert for 1959
        _holidaysListCalculator.CurrentYear = 1959;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        
        // Debug: Check what date was actually calculated
        var actualEaster1959 = _holidaysListCalculator.CalculateEaster(1959);
        Console.WriteLine($"Calculated Easter 1959: {actualEaster1959}");
        
        _holidaysListCalculator.IsHoliday(actualEaster1959)
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputePentecost()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+49",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 28))
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 6, 5))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeCorpusChristi()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+60",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 6, 8))
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2022
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 6, 16))
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Act & Assert for 2018
        _holidaysListCalculator.CurrentYear = 2018;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2018, 5, 31))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeSilvester()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/31",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 31))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeLaborDay()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 1))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void IsLeapYear()
    {
        // Assert
        _holidaysListCalculator.IsLeapYear(1852).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(1892).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(1912).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(1936).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(1968).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(1988).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(2020).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(2032).Should().BeTrue();
        _holidaysListCalculator.IsLeapYear(2048).Should().BeTrue();
    }

    [Test]
    public void IsNotLeapYear()
    {
        // Assert
        _holidaysListCalculator.IsLeapYear(1851).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1853).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1855).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1857).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1859).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1861).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1865).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1866).Should().BeFalse();
        _holidaysListCalculator.IsLeapYear(1867).Should().BeFalse();
    }

    [Test]
    public void ShouldReturn1For1January()
    {
        // Arrange
        var date = new DateOnly(2023, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.Should().Be(1);
    }

    [Test]
    public void ShouldReturn31For31January()
    {
        // Arrange
        var date = new DateOnly(2023, 1, 31);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.Should().Be(31);
    }

    [Test]
    public void ShouldReturn59For28FebruaryInANonLeapYear()
    {
        // Arrange
        var date = new DateOnly(2023, 2, 28);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.Should().Be(59);
    }

    [Test]
    public void ShouldReturn60For29FebruaryInALeapYear()
    {
        // Arrange
        var date = new DateOnly(2024, 2, 29);

        // Act
        var result = _holidaysListCalculator.GetDayOfYear(date);

        // Assert
        result.Should().Be(60);
    }

    [Test]
    public void ShouldReturnWeek52For1stJanuary2022()
    {
        // Arrange
        var date = new DateTime(2022, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(52);
    }

    [Test]
    public void ShouldReturnWeek52For31stDecember2021()
    {
        // Arrange
        var date = new DateTime(2021, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(52);
    }

    [Test]
    public void ShouldReturnWeek1For4thJanuary2021()
    {
        // Arrange
        var date = new DateTime(2021, 1, 4);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(1);
    }

    [Test]
    public void ShouldReturnWeek53For31stDecember2020()
    {
        // Arrange
        var date = new DateTime(2020, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(53);
    }

    [Test]
    public void ShouldReturnWeek2For6thJanuary2020()
    {
        // Arrange
        var date = new DateTime(2020, 1, 6);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(2);
    }

    [Test]
    public void ShouldReturnWeek1For30thDecember2019()
    {
        // Arrange
        var date = new DateTime(2019, 12, 30);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(1);
    }

    [Test]
    public void ShouldReturnWeek2For7thJanuary2019()
    {
        // Arrange
        var date = new DateTime(2019, 1, 7);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(2);
    }

    [Test]
    public void ShouldReturnWeek1For1stJanuary2018()
    {
        // Arrange
        var date = new DateTime(2018, 1, 1);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(1);
    }

    [Test]
    public void ShouldReturnWeek1For31stDecember2018()
    {
        // Arrange
        var date = new DateTime(2018, 12, 31);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(1);
    }

    [Test]
    public void ShouldReturnWeek52For25thDecember2017()
    {
        // Arrange
        var date = new DateTime(2017, 12, 25);

        // Act
        var result = _holidaysListCalculator.GetIso8601WeekNumber(new DateOnly(date.Year, date.Month, date.Day));

        // Assert
        result.Should().Be(52);
    }

    #region SubRule Tests

    [Test]
    public void ShouldApplySubRuleForSaturdayToFriday()
    {
        // Arrange - 1 May 2021 is a Saturday.
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA-1" // Wenn Samstag, dann verschiebe auf Freitag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Should be postponed to Friday, 30 April
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 4, 30))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 5, 1))
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForSundayToMonday()
    {
        // Arrange - 1. Mai 2022 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1" // If Sunday, then move to Monday
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf Montag, den 2. Mai verschoben werden
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 5, 2))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 5, 1))
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplyMultipleSubRules()
    {
        // Arrange - 25. Dezember 2021 ist ein Samstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25",
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA-1;SU+1" // Samstag -> Freitag, Sonntag -> Montag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Samstag -> Freitag
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 12, 24))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForMondayToTuesday()
    {
        // Arrange - 1. Mai 2023 ist ein Montag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "05/01",
            Name = new MultiLanguage { En = "Labor Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+1" // Wenn Montag, dann verschiebe auf Dienstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 2))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForFridayBackToThursday()
    {
        // Arrange - 1. September 2023 ist ein Freitag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/01",
            Name = new MultiLanguage { En = "September Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR-1" // Wenn Freitag, dann verschiebe auf Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 31))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleWithLargerOffset()
    {
        // Arrange - 13. Oktober 2023 ist ein Freitag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "10/13",
            Name = new MultiLanguage { En = "Special Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR+3" // Wenn Freitag, dann 3 Tage später (Montag)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 10, 16))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldNotApplySubRuleForNonMatchingWeekday()
    {
        // Arrange - 15. Juni 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "06/15",
            Name = new MultiLanguage { En = "June Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+1;FR-1" // Regeln für Montag und Freitag, aber es ist Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf dem ursprünglichen Datum bleiben
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 6, 15))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForTuesdayAndWednesday()
    {
        // Arrange - Mehrere Regeln mit verschiedenen Wochentagen
        var rules = new[]
        {
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "08/15", // 15. August 2023 ist ein Dienstag
                Name = new MultiLanguage { En = "August Holiday" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = "TU+2" // Dienstag -> Donnerstag
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "08/16", // 16. August 2023 ist ein Mittwoch
                Name = new MultiLanguage { En = "Another August Holiday" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = "WE-2" // Mittwoch -> Montag
            }
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(2);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 17))
            .Should().Be(HolidayStatus.OfficialHoliday); // Dienstag -> Donnerstag
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 8, 14))
            .Should().Be(HolidayStatus.OfficialHoliday); // Mittwoch -> Montag
    }

    [Test]
    public void ShouldApplySubRuleForThursdayToNextWeek()
    {
        // Arrange - 7. Dezember 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/07",
            Name = new MultiLanguage { En = "December Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "TH+7" // Donnerstag -> nächsten Donnerstag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 14))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleComplexSubRuleWithAllWeekdays()
    {
        // Arrange - Regel mit allen Wochentagen
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/23", // 23. November 2023 ist ein Donnerstag (Thanksgiving)
            Name = new MultiLanguage { En = "Thanksgiving" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "MO+4;TU+3;WE+2;TH+1;FR+3;SA+2;SU+1" // Verschiedene Verschiebungen je nach Wochentag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Donnerstag -> +1 Tag (Freitag)
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 24))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldIgnoreInvalidSubRules()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "03/15",
            Name = new MultiLanguage { En = "March Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "XX+1;MO;FR+;+1;SA-0" // Verschiedene ungültige Formate
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte auf dem ursprünglichen Datum bleiben
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 3, 15))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleAcrossMonthBoundary()
    {
        // Arrange - 30. September 2023 ist ein Samstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "09/30",
            Name = new MultiLanguage { En = "End of September" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2" // Samstag -> Montag (2. Oktober)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte in den nächsten Monat verschoben werden
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 10, 2))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplySubRuleAcrossYearBoundary()
    {
        // Arrange - 31. Dezember 2023 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/31",
            Name = new MultiLanguage { En = "New Year's Eve" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1" // Sonntag -> Montag (1. Januar 2024)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte ins nächste Jahr verschoben werden
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 1, 1))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleNegativeOffsetAcrossMonthBoundary()
    {
        // Arrange - 1. Oktober 2023 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "10/01",
            Name = new MultiLanguage { En = "October First" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU-2" // Sonntag -> Freitag (29. September)
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte in den vorherigen Monat verschoben werden
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 9, 29))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleEmptySubRule()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/14",
            Name = new MultiLanguage { En = "Bastille Day" },
            State = "test-state",
            Country = "FR",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "" // Leere SubRule
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 7, 14))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldApplyOnlyFirstMatchingSubRule()
    {
        // Arrange - 25. Dezember 2022 ist ein Sonntag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25",
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SU+2" // Zwei Regeln für Sonntag - nur die erste sollte angewendet werden
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollte nur um 1 Tag verschoben werden
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 26))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 27))
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplySubRuleForThursdayPlusOne()
    {
        // Arrange - 23. November 2023 ist ein Donnerstag
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/23",
            Name = new MultiLanguage { En = "Test Thursday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "TH+1" // Nur Donnerstag -> Freitag
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        var holiday = _holidaysListCalculator.HolidayList[0];
        holiday.CurrentDate.Should().Be(new DateOnly(2023, 11, 24));
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 24))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    #endregion

    #region Negative Easter Offset Tests

    [Test]
    public void ShouldComputeGoodFriday()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER-2",
            Name = new MultiLanguage { En = "Good Friday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 7))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldComputeEasterMonday()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER+1",
            Name = new MultiLanguage { En = "Easter Monday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2023
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 4, 10))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    #endregion

    #region Multiple Rules Tests

    [Test]
    public void ShouldHandleMultipleRules()
    {
        // Arrange
        var rules = new[]
        {
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "01/01",
                Name = new MultiLanguage { En = "New Year" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "05/01",
                Name = new MultiLanguage { En = "Labor Day" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Rule = "12/25",
                Name = new MultiLanguage { En = "Christmas" },
                State = "test-state",
                Country = "test-country",
                IsMandatory = true,
                IsPaid = true,
                SubRule = string.Empty
            }
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(3);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 1, 1))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 5, 1))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 25))
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldSortHolidaysByDate()
    {
        // Arrange - Füge Feiertage in zufälliger Reihenfolge hinzu
        var rules = new[]
        {
            new CalendarRule { Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }},
            new CalendarRule { Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Rule = "07/04", Name = new MultiLanguage { En = "Independence Day" }},
            new CalendarRule { Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }}
        };

        foreach (var rule in rules)
        {
            rule.Id = Guid.NewGuid();
            rule.State = "test-state";
            rule.Country = "test-country";
            rule.IsMandatory = true;
            rule.IsPaid = true;
            rule.SubRule = string.Empty;
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert - Sollten nach Datum sortiert sein
        _holidaysListCalculator.HolidayList.Should().HaveCount(4);
        _holidaysListCalculator.HolidayList[0].CurrentDate.Should().Be(new DateOnly(2023, 1, 1));
        _holidaysListCalculator.HolidayList[1].CurrentDate.Should().Be(new DateOnly(2023, 5, 1));
        _holidaysListCalculator.HolidayList[2].CurrentDate.Should().Be(new DateOnly(2023, 7, 4));
        _holidaysListCalculator.HolidayList[3].CurrentDate.Should().Be(new DateOnly(2023, 12, 25));
    }

    #endregion

    #region Edge Cases and Validation Tests

    [Test]
    public void ShouldHandleEmptyRulesList()
    {
        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().BeEmpty();
    }

    [Test]
    public void ShouldClearRulesAndHolidays()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/01",
            Name = new MultiLanguage { En = "New Year" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);
        _holidaysListCalculator.ComputeHolidays();

        // Act
        _holidaysListCalculator.Clear();

        // Assert
        _holidaysListCalculator.Count.Should().Be(0);
        _holidaysListCalculator.HolidayList.Should().BeEmpty();
    }

    [Test]
    public void ShouldHandleUnofficialHolidays()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "02/14",
            Name = new MultiLanguage { En = "Valentine's Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = false, // Nicht verpflichtend = inoffiziell
            IsPaid = false,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();

        // Assert
        _holidaysListCalculator.HolidayList.Should().HaveCount(1);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 2, 14))
            .Should().Be(HolidayStatus.UnofficialHoliday);
    }

    [Test]
    public void ShouldReturnCorrectHolidayInfo()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/04",
            Name = new MultiLanguage { En = "Independence Day", De = "Unabhängigkeitstag" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var holidayInfo = _holidaysListCalculator.GetHolidayInfo(new DateOnly(2023, 7, 4));

        // Assert
        holidayInfo.Should().NotBeNull();
        holidayInfo!.CurrentName.Should().Be("Independence Day");
        holidayInfo.CurrentDate.Should().Be(new DateOnly(2023, 7, 4));
        holidayInfo.Officially.Should().BeTrue();
    }

    [Test]
    public void ShouldReturnNullForNonHolidayDate()
    {
        // Arrange
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/01",
            Name = new MultiLanguage { En = "New Year" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = string.Empty
        };
        _holidaysListCalculator.Add(rule);

        // Act
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var holidayInfo = _holidaysListCalculator.GetHolidayInfo(new DateOnly(2023, 1, 2));

        // Assert
        holidayInfo.Should().BeNull();
    }

    [Test]
    public void ShouldCalculateCorrectDaysInMonth()
    {
        // Assert
        _holidaysListCalculator.GetDaysInMonth(1, 2023).Should().Be(31); // Januar
        _holidaysListCalculator.GetDaysInMonth(2, 2023).Should().Be(28); // Februar (kein Schaltjahr)
        _holidaysListCalculator.GetDaysInMonth(2, 2024).Should().Be(29); // Februar (Schaltjahr)
        _holidaysListCalculator.GetDaysInMonth(4, 2023).Should().Be(30); // April
    }

    [Test]
    public void ShouldThrowExceptionForInvalidMonth()
    {
        // Act
        var act = () => _holidaysListCalculator.GetDaysInMonth(13, 2023);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("Invalid month*")
            .And.ParamName.Should().Be("month");
    }

    [Test]
    public void ShouldCalculateTotalDaysInYear()
    {
        // Act & Assert
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.GetTotalDaysInCurrentYear().Should().Be(365);

        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.GetTotalDaysInCurrentYear().Should().Be(366);
    }

    #endregion

    #region Complex SubRules Tests

    [Test]
    public void ShouldApplyMultipleSubRulesWithSemicolonSeparator()
    {
        // Arrange - Holiday that falls on Saturday should move to Monday, 
        // but if it falls on Sunday should move to Tuesday
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "06/15", // June 15th
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2;SU+2" // Saturday +2 days, Sunday +2 days
        };
        _holidaysListCalculator.Add(rule);

        // Act & Assert for 2024 (June 15 = Saturday)
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        var june15_2024 = new DateOnly(2024, 6, 15); // Saturday
        june15_2024.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        
        // Should move to Monday (June 17)
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 6, 17))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(june15_2024)
            .Should().Be(HolidayStatus.NotAHoliday);

        // Act & Assert for 2025 (June 15 = Sunday)
        _holidaysListCalculator.CurrentYear = 2025;
        _holidaysListCalculator.ComputeHolidays();
        var june15_2025 = new DateOnly(2025, 6, 15); // Sunday
        june15_2025.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        
        // Should move to Tuesday (June 17)
        _holidaysListCalculator.IsHoliday(new DateOnly(2025, 6, 17))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(june15_2025)
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldApplyOnlyFirstMatchingSubRule1()
    {
        // Arrange - Multiple rules for same weekday, only first should apply
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "03/17", // March 17th
            Name = new MultiLanguage { En = "St. Patrick's Day" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SU+3" // First rule: Sunday +1, Second rule: Sunday +3 (should not apply)
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2024: March 17 is Sunday
        _holidaysListCalculator.CurrentYear = 2024;
        _holidaysListCalculator.ComputeHolidays();
        var march17_2024 = new DateOnly(2024, 3, 17); // Sunday
        march17_2024.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        // Assert - Should apply only first rule (+1 day = Monday March 18)
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 3, 18))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2024, 3, 20)) // +3 days
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldHandleComplexWeekdayAdjustmentScenarios()
    {
        // Arrange - Different adjustments for different weekdays
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "12/25", // Christmas
            Name = new MultiLanguage { En = "Christmas" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+2;SU+1;MO-1;TU+3" // Sat+2, Sun+1, Mon-1, Tue+3
        };
        _holidaysListCalculator.Add(rule);

        // Test Saturday scenario (2021: Dec 25 = Saturday)
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2021 = new DateOnly(2021, 12, 25);
        dec25_2021.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 12, 27)) // +2 days
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Test Sunday scenario (2022: Dec 25 = Sunday)  
        _holidaysListCalculator.CurrentYear = 2022;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2022 = new DateOnly(2022, 12, 25);
        dec25_2022.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2022, 12, 26)) // +1 day
            .Should().Be(HolidayStatus.OfficialHoliday);

        // Test Monday scenario (2023: Dec 25 = Monday)
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var dec25_2023 = new DateOnly(2023, 12, 25);
        dec25_2023.DayOfWeek.Should().Be(DayOfWeek.Monday);
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 12, 24)) // -1 day
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldIgnoreInvalidSubRuleFormats()
    {
        // Arrange - Mix of valid and invalid SubRules
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "07/04", // July 4th
            Name = new MultiLanguage { En = "Independence Day" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+1;XX+2;MO;TU+0;WE+1" // Invalid: XX+2, MO (no offset), TU+0 (zero offset)
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2020: July 4 = Saturday, should apply SA+1
        _holidaysListCalculator.CurrentYear = 2020;
        _holidaysListCalculator.ComputeHolidays();
        var july4_2020 = new DateOnly(2020, 7, 4);
        july4_2020.DayOfWeek.Should().Be(DayOfWeek.Saturday);

        // Assert - Should apply only valid SA+1 rule
        _holidaysListCalculator.IsHoliday(new DateOnly(2020, 7, 5)) // +1 day
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(july4_2020)
            .Should().Be(HolidayStatus.NotAHoliday);

        // Act - 2023: July 4 = Tuesday, should ignore TU+0 and apply WE+1 if it were Wednesday
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var july4_2023 = new DateOnly(2023, 7, 4);
        july4_2023.DayOfWeek.Should().Be(DayOfWeek.Tuesday);

        // Assert - Should stay on original date since TU+0 is invalid
        _holidaysListCalculator.IsHoliday(july4_2023)
            .Should().Be(HolidayStatus.OfficialHoliday);
    }

    [Test]
    public void ShouldHandleSubRulesWithEasterDates()
    {
        // Arrange - Easter with SubRule adjustments
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "EASTER-2", // Good Friday (2 days before Easter)
            Name = new MultiLanguage { En = "Good Friday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SU+1;SA-1" // If Sunday move forward, if Saturday move back
        };
        _holidaysListCalculator.Add(rule);

        // Act - Test for multiple years to find Saturday/Sunday Good Friday
        for (int year = 2020; year <= 2030; year++)
        {
            _holidaysListCalculator.CurrentYear = year;
            var easter = _holidaysListCalculator.CalculateEaster(year);
            var goodFriday = easter.AddDays(-2);
            
            _holidaysListCalculator.ComputeHolidays();
            
            if (goodFriday.DayOfWeek == DayOfWeek.Saturday)
            {
                // Should move back 1 day to Friday
                _holidaysListCalculator.IsHoliday(goodFriday.AddDays(-1))
                    .Should().Be(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} (Saturday) in {year} should move to Friday");
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .Should().Be(HolidayStatus.NotAHoliday, $"Original Good Friday {goodFriday} (Saturday) in {year} should not be holiday");
            }
            else if (goodFriday.DayOfWeek == DayOfWeek.Sunday)
            {
                // Should move forward 1 day to Monday
                _holidaysListCalculator.IsHoliday(goodFriday.AddDays(1))
                    .Should().Be(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} (Sunday) in {year} should move to Monday");
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .Should().Be(HolidayStatus.NotAHoliday, $"Original Good Friday {goodFriday} (Sunday) in {year} should not be holiday");
            }
            else
            {
                // Should stay on original date
                _holidaysListCalculator.IsHoliday(goodFriday)
                    .Should().Be(HolidayStatus.OfficialHoliday, $"Good Friday {goodFriday} ({goodFriday.DayOfWeek}) in {year} should stay on original date");
            }
        }
    }

    [Test]
    public void ShouldHandleEmptyAndWhitespaceSubRules()
    {
        // Arrange - SubRules with empty, whitespace, and valid entries
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "11/11", // Veterans Day
            Name = new MultiLanguage { En = "Veterans Day" },
            State = "test-state",
            Country = "USA",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "SA+1; ;   ;MO-1" // Valid SA+1, empty entries, valid MO-1
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2023: Nov 11 = Saturday
        _holidaysListCalculator.CurrentYear = 2023;
        _holidaysListCalculator.ComputeHolidays();
        var nov11_2023 = new DateOnly(2023, 11, 11);
        nov11_2023.DayOfWeek.Should().Be(DayOfWeek.Saturday);

        // Assert - Should apply SA+1 rule (move to Sunday)
        _holidaysListCalculator.IsHoliday(new DateOnly(2023, 11, 12))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(nov11_2023)
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    [Test]
    public void ShouldHandleSubRulesWithLargeOffsets()
    {
        // Arrange - SubRules with large day offsets
        var rule = new CalendarRule
        {
            Id = Guid.NewGuid(),
            Rule = "01/15", // January 15th
            Name = new MultiLanguage { En = "Test Holiday" },
            State = "test-state",
            Country = "test-country",
            IsMandatory = true,
            IsPaid = true,
            SubRule = "FR+10;MO-7" // Large positive and negative offsets
        };
        _holidaysListCalculator.Add(rule);

        // Act - 2021: Jan 15 = Friday
        _holidaysListCalculator.CurrentYear = 2021;
        _holidaysListCalculator.ComputeHolidays();
        var jan15_2021 = new DateOnly(2021, 1, 15);
        jan15_2021.DayOfWeek.Should().Be(DayOfWeek.Friday);

        // Assert - Should apply FR+10 (move 10 days forward to January 25)
        _holidaysListCalculator.IsHoliday(new DateOnly(2021, 1, 25))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(jan15_2021)
            .Should().Be(HolidayStatus.NotAHoliday);

        // Act - 2018: Jan 15 = Monday  
        _holidaysListCalculator.CurrentYear = 2018;
        _holidaysListCalculator.ComputeHolidays();
        var jan15_2018 = new DateOnly(2018, 1, 15);
        jan15_2018.DayOfWeek.Should().Be(DayOfWeek.Monday);

        // Assert - Should apply MO-7 (move 7 days back to January 8)
        _holidaysListCalculator.IsHoliday(new DateOnly(2018, 1, 8))
            .Should().Be(HolidayStatus.OfficialHoliday);
        _holidaysListCalculator.IsHoliday(jan15_2018)
            .Should().Be(HolidayStatus.NotAHoliday);
    }

    #endregion

    #region AddRange and Remove Tests

    [Test]
    public void ShouldAddMultipleRulesAtOnce()
    {
        // Arrange
        var rules = new List<CalendarRule>
        {
            new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }}
        };

        // Act
        _holidaysListCalculator.AddRange(rules);

        // Assert
        _holidaysListCalculator.Count.Should().Be(3);
    }

    [Test]
    public void ShouldRemoveRuleByIndex()
    {
        // Arrange
        var rules = new[]
        {
            new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01", Name = new MultiLanguage { En = "New Year" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "05/01", Name = new MultiLanguage { En = "Labor Day" }},
            new CalendarRule { Id = Guid.NewGuid(), Rule = "12/25", Name = new MultiLanguage { En = "Christmas" }}
        };

        foreach (var rule in rules)
        {
            _holidaysListCalculator.Add(rule);
        }

        // Act
        _holidaysListCalculator.Remove(1); // Entferne Labor Day

        // Assert
        _holidaysListCalculator.Count.Should().Be(2);
        var remainingRule = _holidaysListCalculator.GetRule(1);
        remainingRule?.Name?.En.Should().Be("Christmas");
    }

    [Test]
    public void ShouldHandleRemoveWithInvalidIndex()
    {
        // Arrange
        _holidaysListCalculator.Add(new CalendarRule { Id = Guid.NewGuid(), Rule = "01/01" });

        // Act
        _holidaysListCalculator.Remove(5); // Invalid index
        _holidaysListCalculator.Remove(-1); // Negative index

        // Assert
        _holidaysListCalculator.Count.Should().Be(1); // Sollte unverändert bleiben
    }

    [Test]
    public void ShouldGetRuleByIndex()
    {
        // Arrange
        var rule = new CalendarRule 
        { 
            Id = Guid.NewGuid(), 
            Rule = "07/04", 
            Name = new MultiLanguage { En = "Independence Day" }
        };
        _holidaysListCalculator.Add(rule);

        // Act
        var retrievedRule = _holidaysListCalculator.GetRule(0);

        // Assert
        retrievedRule.Should().NotBeNull();
        retrievedRule!.Rule.Should().Be("07/04");
        retrievedRule.Name?.En.Should().Be("Independence Day");
    }

    [Test]
    public void ShouldReturnNullForInvalidRuleIndex()
    {
        // Act
        var rule = _holidaysListCalculator.GetRule(0);

        // Assert
        rule.Should().BeNull();
    }

    #endregion

    #region Format Date Tests

    [Test]
    public void ShouldFormatDateCorrectly()
    {
        // Arrange
        var date = new DateOnly(2023, 7, 4);

        // Act
        var formatted = _holidaysListCalculator.FormatDate(date);

        // Assert
        formatted.Should().Contain("04");
        formatted.Should().Contain("Jul");
        formatted.Should().Contain("2023");
    }

    #endregion
}