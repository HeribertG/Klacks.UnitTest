// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests ContainerAutofillTravelTimeFloor: the minimum travel time setting is stored with a dot as decimal separator
/// and must be read the same way whatever the server culture is (de-DE reads "1.5" as 15 when parsed culture-bound).
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillTravelTimeFloorTests
{
    [TestCase("de-DE")]
    [TestCase("fr-FR")]
    [TestCase("en-US")]
    public async Task FractionalMinutes_AreReadCultureInvariant(string cultureName)
    {
        // Arrange
        var previous = CultureInfo.CurrentCulture;
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(SettingKeys.RouteMinTravelTimeByCar)
            .Returns(new SettingsEntity { Type = SettingKeys.RouteMinTravelTimeByCar, Value = "1.5" });
        var matrix = new double[,] { { 0, 60 }, { 60, 0 } };

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            // Act
            await new ContainerAutofillTravelTimeFloor(settingsReader).ApplyAsync(matrix, ContainerTransportMode.ByCar);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // Assert
        matrix[0, 1].ShouldBe(90);
        matrix[1, 0].ShouldBe(90);
        matrix[0, 0].ShouldBe(0);
    }
}
