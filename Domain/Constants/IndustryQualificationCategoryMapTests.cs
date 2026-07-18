// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class IndustryQualificationCategoryMapTests
{
    [TestCase(IndustrySlugs.Homecare, QualificationCategory.Spitex)]
    [TestCase("spitex", QualificationCategory.Spitex)]
    [TestCase(IndustrySlugs.Healthcare, QualificationCategory.Healthcare)]
    [TestCase("spitaeler", QualificationCategory.Healthcare)]
    [TestCase("hospitals", QualificationCategory.Healthcare)]
    [TestCase(IndustrySlugs.Security, QualificationCategory.Security)]
    [TestCase(IndustrySlugs.Facility, QualificationCategory.Cleaning)]
    [TestCase("cleaning", QualificationCategory.Cleaning)]
    [TestCase(IndustrySlugs.Logistics, QualificationCategory.Logistics)]
    [TestCase("logistik", QualificationCategory.Logistics)]
    [TestCase("gastronomy", QualificationCategory.Gastronomy)]
    [TestCase("gastro", QualificationCategory.Gastronomy)]
    [TestCase("construction", QualificationCategory.Construction)]
    [TestCase("transport", QualificationCategory.Transport)]
    [TestCase("unknown-industry", QualificationCategory.Others)]
    public void Resolve_KnownAndUnknownSlugs_MapsToExpectedCategory(string slug, QualificationCategory expected)
    {
        IndustryQualificationCategoryMap.Resolve(slug).ShouldBe(expected);
    }
}
