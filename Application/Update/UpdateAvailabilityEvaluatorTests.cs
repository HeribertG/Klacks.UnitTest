// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Update;

using Klacks.Api.Application.Services.Update;
using Klacks.Api.Domain.Models.Update;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class UpdateAvailabilityEvaluatorTests
{
    private UpdateAvailabilityEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new UpdateAvailabilityEvaluator();
    }

    private static UpdateManifest Manifest(string latest, string minUpgradableFrom, bool containsMigrations = false)
    {
        return new UpdateManifest
        {
            Channel = UpdateChannel.Stable,
            LatestVersion = latest,
            MinUpgradableFrom = minUpgradableFrom,
            ContainsMigrations = containsMigrations,
        };
    }

    [Test]
    public void Same_version_is_up_to_date()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(1, 2, 0), Manifest("1.2.0", "1.0.0"));

        result.Status.ShouldBe(UpdateAvailabilityStatus.UpToDate);
        result.IsUpdateAvailable.ShouldBeFalse();
    }

    [Test]
    public void Newer_manifest_within_upgrade_range_is_available()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(1, 0, 0), Manifest("1.2.0", "1.0.0", containsMigrations: true));

        result.Status.ShouldBe(UpdateAvailabilityStatus.UpdateAvailable);
        result.IsUpdateAvailable.ShouldBeTrue();
        result.ContainsMigrations.ShouldBeTrue();
        result.CurrentVersion.ShouldBe("1.0.0");
        result.LatestVersion.ShouldBe("1.2.0");
    }

    [Test]
    public void Current_below_min_upgradable_requires_intermediate()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(0, 9, 0), Manifest("1.2.0", "1.0.0"));

        result.Status.ShouldBe(UpdateAvailabilityStatus.UpdateRequiresIntermediate);
        result.IsUpdateAvailable.ShouldBeTrue();
    }

    [Test]
    public void Current_newer_than_manifest_is_up_to_date()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(2, 0, 0), Manifest("1.9.9", "1.0.0"));

        result.Status.ShouldBe(UpdateAvailabilityStatus.UpToDate);
    }

    [Test]
    public void Invalid_manifest_version_is_reported()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(1, 0, 0), Manifest("not-a-version", "1.0.0"));

        result.Status.ShouldBe(UpdateAvailabilityStatus.ManifestInvalid);
        result.IsUpdateAvailable.ShouldBeFalse();
    }

    [Test]
    public void Missing_min_upgradable_from_does_not_gate_update()
    {
        var result = _evaluator.Evaluate(new SemanticVersion(0, 1, 0), Manifest("1.2.0", string.Empty));

        result.Status.ShouldBe(UpdateAvailabilityStatus.UpdateAvailable);
    }
}
