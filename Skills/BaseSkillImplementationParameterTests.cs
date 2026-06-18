// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BaseSkillImplementation.GetParameter: tool-call arguments arrive as JsonElement
/// (Dictionary&lt;string, object&gt; deserialization), so numeric and boolean parameters must be unwrapped
/// instead of silently falling back to their default.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class BaseSkillImplementationParameterTests
{
    private sealed class ParameterProbe : BaseSkillImplementation
    {
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
    public void Reads_StringFromJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["canton"] = JsonSerializer.SerializeToElement("BE") };

        Assert.That(ParameterProbe.Read<string>(parameters, "canton"), Is.EqualTo("BE"));
    }

    [Test]
    public void Reads_GuidFromJsonElement()
    {
        var id = Guid.NewGuid();
        var parameters = new Dictionary<string, object> { ["id"] = JsonSerializer.SerializeToElement(id.ToString()) };

        Assert.That(ParameterProbe.Read<Guid?>(parameters, "id"), Is.EqualTo(id));
    }

    [Test]
    public void ReturnsDefault_WhenJsonElementIsNull()
    {
        var parameters = new Dictionary<string, object> { ["count"] = JsonSerializer.SerializeToElement((int?)null) };

        Assert.That(ParameterProbe.Read<int?>(parameters, "count"), Is.Null);
    }

    [Test]
    public void StillReads_NativeBool_NotWrappedInJsonElement()
    {
        var parameters = new Dictionary<string, object> { ["apply"] = true };

        Assert.That(ParameterProbe.Read<bool?>(parameters, "apply"), Is.True);
    }
}
