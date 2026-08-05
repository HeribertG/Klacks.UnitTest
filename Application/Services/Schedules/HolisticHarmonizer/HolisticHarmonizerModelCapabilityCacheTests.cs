// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public sealed class HolisticHarmonizerModelCapabilityCacheTests
{
    private const string ModelId = "stub-model";

    [Test]
    public void TryGet_UnknownModel_ReturnsFalse()
    {
        var cache = new HolisticHarmonizerModelCapabilityCache();

        cache.TryGet(ModelId, out var capable, out var error).ShouldBeFalse();
        capable.ShouldBeFalse();
        error.ShouldBeNull();
    }

    [Test]
    public void Store_PositiveVerdict_IsReturnedOnLookup()
    {
        var cache = new HolisticHarmonizerModelCapabilityCache();
        cache.Store(ModelId, isVisionCapable: true, error: null);

        cache.TryGet(ModelId, out var capable, out var error).ShouldBeTrue();
        capable.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Test]
    public void Store_NegativeVerdict_KeepsTheReason()
    {
        var cache = new HolisticHarmonizerModelCapabilityCache();
        cache.Store(ModelId, isVisionCapable: false, error: "model cannot read images");

        cache.TryGet(ModelId, out var capable, out var error).ShouldBeTrue();
        capable.ShouldBeFalse();
        error.ShouldBe("model cannot read images");
    }

    [Test]
    public void Store_OverwritesAnEarlierVerdict()
    {
        var cache = new HolisticHarmonizerModelCapabilityCache();
        cache.Store(ModelId, isVisionCapable: false, error: "temporary outage");
        cache.Store(ModelId, isVisionCapable: true, error: null);

        cache.TryGet(ModelId, out var capable, out var error).ShouldBeTrue();
        capable.ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Test]
    public void Verdicts_AreKeptPerModel()
    {
        var cache = new HolisticHarmonizerModelCapabilityCache();
        cache.Store("vision-model", isVisionCapable: true, error: null);
        cache.Store("text-model", isVisionCapable: false, error: "text only");

        cache.TryGet("vision-model", out var visionCapable, out _).ShouldBeTrue();
        cache.TryGet("text-model", out var textCapable, out var textError).ShouldBeTrue();

        visionCapable.ShouldBeTrue();
        textCapable.ShouldBeFalse();
        textError.ShouldBe("text only");
    }
}
