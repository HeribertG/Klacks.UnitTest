// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class OpenOrderExportSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    [Test]
    public async Task Open_WithoutFormat_ReturnsNavigationToPeriodClosing()
    {
        var skill = new OpenOrderExportSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Navigation);
        result.Data!.ToString()!.ShouldContain("/workplace/period-closing");
        result.Message.ShouldContain("period-closing");
    }

    [Test]
    public async Task Open_WithUppercaseFormat_NormalizesAndSucceeds()
    {
        var skill = new OpenOrderExportSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["format"] = "CSV"
        });

        result.Success.ShouldBeTrue();
        result.Type.ShouldBe(SkillResultType.Navigation);
        result.Message.ShouldContain("csv");
    }

    [Test]
    public async Task Open_WithUnsupportedFormat_ReturnsError()
    {
        var skill = new OpenOrderExportSkill();

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["format"] = "excel"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Unsupported export format 'excel'");
        result.Message.ShouldContain("csv");
    }
}
