// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateAbsenceTypeSkill.ExecuteAsync, written before its long body was split into
/// private steps: the defaultLength/defaultValue refusals after name, abbreviation and color were already applied to
/// the loaded type, the no-op text and data, the changed-field order, the audit fields, the exact success and
/// verification-failure texts, the order put -> complete -> re-read, and the tolerance of the defaultValue check.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAbsenceTypeSkillCharacterizationTests
{
    private static readonly Guid TypeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IAbsenceRepository _absenceRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateAbsenceTypeSkill _skill = null!;
    private Absence _type = null!;
    private Absence? _reread;

    [SetUp]
    public void Setup()
    {
        _type = Type();
        _reread = null;

        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _absenceRepository.Get(TypeId).Returns(_ => _type);
        _absenceRepository.GetNoTracking(TypeId).Returns(_ => _reread ?? _type);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _skill = new UpdateAbsenceTypeSkill(_absenceRepository, _unitOfWork);
    }

    private static Absence Type()
    {
        var name = new MultiLanguage();
        var abbreviation = new MultiLanguage();
        foreach (var language in MultiLanguage.CoreLanguages)
        {
            name.SetValue(language, "Ferien");
            abbreviation.SetValue(language, "FE");
        }

        return new Absence
        {
            Id = TypeId,
            Name = name,
            Abbreviation = abbreviation,
            Description = new MultiLanguage(),
            Color = "#AA0000",
            DefaultLength = 1,
            DefaultValue = 0.5
        };
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["absenceTypeId"] = TypeId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task NegativeDefaultLength_IsRefused_AfterTextFieldsWereApplied()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("name", "Urlaub"), ("abbreviation", "UR"), ("color", "#123456"), ("defaultLength", -1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("defaultLength must be >= 0.");
        _type.Name.De.ShouldBe("Urlaub");
        _type.Name.En.ShouldBe("Urlaub");
        _type.Abbreviation.De.ShouldBe("UR");
        _type.Color.ShouldBe("#123456");
        _type.UpdateTime.ShouldBeNull();
        await _absenceRepository.DidNotReceive().Put(Arg.Any<Absence>());
    }

    [TestCase(1.5)]
    [TestCase(-0.1)]
    public async Task DefaultValueOutOfRange_IsRefused_AfterEarlierFieldsWereApplied(double value)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("color", "#123456"), ("defaultValue", (decimal)value)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("defaultValue must be between 0 and 1.");
        _type.Color.ShouldBe("#123456");
        await _absenceRepository.DidNotReceive().Put(Arg.Any<Absence>());
    }

    [Test]
    public async Task UnchangedValues_ReturnNoOp_WithoutAuditOrPut()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("name", "Ferien"),
            ("color", "#AA0000"),
            ("defaultLength", 1),
            ("defaultValue", 0.5m),
            ("withSaturday", false),
            ("hideInGantt", false)));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — absence type left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"AbsenceTypeId\":\"" + TypeId + "\",\"ChangedFields\":[]}");
        _type.UpdateTime.ShouldBeNull();
        await _absenceRepository.DidNotReceive().Put(Arg.Any<Absence>());
    }

    [Test]
    public async Task AllKindsOfFields_AreReportedInFixedOrder_WithAuditFieldsAndExactSuccess()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("hideInGantt", true),
            ("isOnCall", true),
            ("withSunday", true),
            ("defaultValue", 1m),
            ("defaultLength", 3),
            ("color", "#123456"),
            ("abbreviation", "UR"),
            ("name", "Urlaub")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Absence type 'Urlaub' updated (name, abbreviation, color, defaultLength, defaultValue, withSunday, isOnCall, hideInGantt) "
            + "and confirmed in the database (verified). The change affects future bookings; existing bookings keep their values.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"AbsenceTypeId\":\"" + TypeId + "\","
            + "\"ChangedFields\":[\"name\",\"abbreviation\",\"color\",\"defaultLength\",\"defaultValue\",\"withSunday\",\"isOnCall\",\"hideInGantt\"],"
            + "\"Name\":\"Urlaub\",\"Abbreviation\":\"UR\",\"Color\":\"#123456\"}");
        _type.Name.En.ShouldBe("Urlaub");
        _type.DefaultLength.ShouldBe(3);
        _type.DefaultValue.ShouldBe(1d);
        _type.WithSunday.ShouldBeTrue();
        _type.IsOnCall.ShouldBeTrue();
        _type.HideInGantt.ShouldBeTrue();
        _type.WithSaturday.ShouldBeFalse();
        _type.CurrentUserUpdated.ShouldBe("tester");
        _type.UpdateTime.ShouldNotBeNull();
        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
            _absenceRepository.Put(_type);
            _unitOfWork.CompleteAsync();
            _absenceRepository.GetNoTracking(TypeId);
        });
    }

    [Test]
    public async Task ReReadWithOldColor_FailsTheVerification()
    {
        _reread = Type();

        var result = await _skill.ExecuteAsync(Ctx(), Params(("color", "#123456")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Database verification failed: the update (color) of absence type 'Ferien' could not be confirmed in the database "
            + "after the write — the change was rolled back.");
    }

    [Test]
    public async Task ReReadWithOldFlag_FailsTheVerification()
    {
        _reread = Type();

        var result = await _skill.ExecuteAsync(Ctx(), Params(("name", "Urlaub"), ("hideInGantt", true)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Database verification failed: the update (name, hideInGantt) of absence type 'Urlaub' could not be confirmed in the database "
            + "after the write — the change was rolled back.");
    }

    [TestCase(0.75005, true)]
    [TestCase(0.7502, false)]
    public async Task DefaultValueVerification_ToleratesTinyRoundingOnly(double persistedValue, bool verified)
    {
        _reread = Type();
        _reread.DefaultValue = persistedValue;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("defaultValue", 0.75m)));

        result.Success.ShouldBe(verified, result.Message);
    }
}
