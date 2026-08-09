// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BaseSkill.GetParameter. BaseSkill carries the generated settings-reader skills
/// (SettingsReaderSkillBase), and its parameter reading used to lack the JsonElement unwrapping that
/// BaseSkillImplementation had: a tool-call argument would then fall back to its default instead of
/// its value. Both classes now share SkillParameterReader; these tests pin that they agree.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BaseSkillParameterTests
{
    private sealed class ParameterProbe : BaseSkill
    {
        public override string Name => nameof(ParameterProbe);
        public override string Description => string.Empty;
        public override SkillCategory Category => SkillCategory.Query;
        public override IReadOnlyList<SkillParameter> Parameters => Array.Empty<SkillParameter>();

        public override Task<SkillResult> ExecuteAsync(
            SkillExecutionContext context,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public static T? Read<T>(Dictionary<string, object> parameters, string name) =>
            GetParameter<T>(parameters, name);
    }

    [Test]
    public void Reads_BoolFromJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["apply"] = JsonSerializer.SerializeToElement(true) };

        Assert.That(ParameterProbe.Read<bool?>(parameters, "apply"), Is.True);
    }

    [Test]
    public void Reads_IntFromJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["count"] = JsonSerializer.SerializeToElement(3) };

        Assert.That(ParameterProbe.Read<int?>(parameters, "count"), Is.EqualTo(3));
    }

    [Test]
    public void Reads_DecimalFromJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["hours"] = JsonSerializer.SerializeToElement(7.5m) };

        Assert.That(ParameterProbe.Read<decimal?>(parameters, "hours"), Is.EqualTo(7.5m));
    }

    [Test]
    public void Reads_StringFromJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["name"] = JsonSerializer.SerializeToElement("Meier") };

        Assert.That(ParameterProbe.Read<string>(parameters, "name"), Is.EqualTo("Meier"));
    }

    [Test]
    public void MissingParameter_ReturnsDefault()
    {
        Assert.That(ParameterProbe.Read<int?>(new Dictionary<string, object>(), "count"), Is.Null);
    }

    [Test]
    public void UnconvertibleValue_ReturnsDefault()
    {
        var parameters = new Dictionary<string, object> { ["count"] = "not a number" };

        Assert.That(ParameterProbe.Read<int?>(parameters, "count"), Is.Null);
    }
}
