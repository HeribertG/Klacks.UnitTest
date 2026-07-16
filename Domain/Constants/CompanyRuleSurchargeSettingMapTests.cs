// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class CompanyRuleSurchargeSettingMapTests
{
    [Test]
    public void Map_CoversEverySurchargeCatalogParameter()
    {
        var catalog = new CompanyRuleParameterCatalog();
        var surchargeParameters = catalog.GetParameters(CompanyRuleKind.SurchargeSettings)
            .Select(p => p.Name)
            .ToList();

        foreach (var name in surchargeParameters)
        {
            CompanyRuleSurchargeSettingMap.ParameterToSettingKey.ContainsKey(name)
                .ShouldBeTrue($"surcharge parameter '{name}' has no setting-key mapping");
        }
    }

    [Test]
    public void Map_HasNoOrphanEntriesOutsideTheCatalog()
    {
        var catalog = new CompanyRuleParameterCatalog();
        var surchargeParameters = catalog.GetParameters(CompanyRuleKind.SurchargeSettings)
            .Select(p => p.Name)
            .ToHashSet();

        foreach (var mapped in CompanyRuleSurchargeSettingMap.ParameterToSettingKey.Keys)
        {
            surchargeParameters.ShouldContain(mapped);
        }
    }
}
