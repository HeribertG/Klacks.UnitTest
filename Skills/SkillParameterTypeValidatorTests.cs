// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillParameterTypeValidator: values that are parsable as their declared type pass
/// (including string representations like "5" for an Integer and "today" for a Date), unparsable
/// values fail loudly with parameter name, expected type and received value, and null/blank/undeclared
/// parameters are left to the skill itself.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillParameterTypeValidatorTests
{
    private static SkillDescriptor Descriptor(params SkillParameter[] parameters) =>
        new("test_skill", "test", SkillCategory.Crud, parameters, [], [], null);

    private static SkillParameter Param(string name, SkillParameterType type) =>
        new(name, "test", type, Required: false);

    private static Dictionary<string, object> With(string name, object value) =>
        new() { [name] = value };

    [TestCase(5)]
    [TestCase("5")]
    [TestCase("-12")]
    public void Integer_ParsableValues_Pass(object value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)), With("count", value));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Integer_JsonElementNumber_Passes()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)),
            With("count", JsonSerializer.SerializeToElement(7)));

        Assert.That(errors, Is.Empty);
    }

    [TestCase("abc")]
    [TestCase("5.5")]
    public void Integer_UnparsableValues_FailWithNameTypeAndValue(string value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)), With("count", value));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("'count'").And.Contain("integer").And.Contain(value));
    }

    [Test]
    public void Integer_FractionalJsonNumber_Fails()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)),
            With("count", JsonSerializer.SerializeToElement(5.5)));

        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [TestCase("7.5")]
    [TestCase(7.5)]
    [TestCase(8)]
    public void Decimal_ParsableValues_Pass(object value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("workTime", SkillParameterType.Decimal)), With("workTime", value));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Decimal_UnparsableValue_Fails()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("workTime", SkillParameterType.Decimal)), With("workTime", "eight"));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("'workTime'").And.Contain("eight"));
    }

    [TestCase(true)]
    [TestCase("true")]
    [TestCase("False")]
    public void Boolean_ParsableValues_Pass(object value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("apply", SkillParameterType.Boolean)), With("apply", value));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Boolean_JsonElementTrue_Passes()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("apply", SkillParameterType.Boolean)),
            With("apply", JsonSerializer.SerializeToElement(true)));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Boolean_UnparsableValue_Fails()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("apply", SkillParameterType.Boolean)), With("apply", "yes please"));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("'apply'").And.Contain("boolean"));
    }

    [TestCase("2026-08-01")]
    [TestCase("31.12.2026")]
    [TestCase("today")]
    [TestCase("heute")]
    public void Date_ParsableValuesAndTodayWords_Pass(string value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("date", SkillParameterType.Date)), With("date", value));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Date_UnparsableValue_Fails()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("date", SkillParameterType.Date)), With("date", "not-a-date"));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("'date'").And.Contain("not-a-date"));
    }

    [TestCase("08:30")]
    [TestCase("23:59")]
    public void Time_ParsableValues_Pass(string value)
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("startTime", SkillParameterType.Time)), With("startTime", value));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Time_UnparsableValue_Fails()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("startTime", SkillParameterType.Time)), With("startTime", "morning"));

        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("'startTime'").And.Contain("morning"));
    }

    [Test]
    public void DateTime_ParsableValue_Passes()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("timestamp", SkillParameterType.DateTime)),
            With("timestamp", "2026-08-01T08:30"));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void String_AnyValue_Passes()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("name", SkillParameterType.String)), With("name", 42));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Enum_AnyStringValue_Passes()
    {
        var descriptor = Descriptor(new SkillParameter(
            "canton", "test", SkillParameterType.Enum, Required: false,
            EnumValues: new[] { "BE", "ZH" }));

        var errors = SkillParameterTypeValidator.Validate(descriptor, With("canton", "JU"));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void NullValue_IsLeftToTheSkill()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)),
            With("count", JsonSerializer.SerializeToElement((int?)null)));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void BlankString_IsLeftToTheSkill()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("date", SkillParameterType.Date)), With("date", "  "));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void UndeclaredParameter_IsIgnored()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)), With("other", "abc"));

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void MissingDeclaredParameter_IsIgnored()
    {
        var errors = SkillParameterTypeValidator.Validate(
            Descriptor(Param("count", SkillParameterType.Integer)),
            new Dictionary<string, object>());

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void MultipleInvalidParameters_ReportEveryError()
    {
        var descriptor = Descriptor(
            Param("count", SkillParameterType.Integer),
            Param("date", SkillParameterType.Date));
        var parameters = new Dictionary<string, object>
        {
            ["count"] = "abc",
            ["date"] = "not-a-date"
        };

        var errors = SkillParameterTypeValidator.Validate(descriptor, parameters);

        Assert.That(errors, Has.Count.EqualTo(2));
    }
}
