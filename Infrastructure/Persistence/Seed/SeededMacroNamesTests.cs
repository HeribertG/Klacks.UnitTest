// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards SeededMacroNames against drift: the constant list must hold exactly the names the seed code gives the
/// template rows (MacrosSeed plus the AddAllShiftAdditiveMacro migration), one per template id.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class SeededMacroNamesTests
{
    [Test]
    public void Names_MatchTheSeedCode_ForEveryTemplateId()
    {
        var seededNames = SeededMacroScripts.NamesById();

        seededNames.Keys.ShouldBe(SeededMacroIds.All, ignoreOrder: true);
        SeededMacroNames.All.ShouldBe(seededNames.Values, ignoreOrder: true);
    }
}
