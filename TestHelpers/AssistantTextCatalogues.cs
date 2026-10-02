// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Resets every static text catalogue that AssistantTextsPluginLoader.Load fills, so a test that loads the
/// packs leaves no configured language behind for the next fixture.
/// </summary>
internal static class AssistantTextCatalogues
{
    public static void ResetAll()
    {
        EscalationHandoffTexts.Reset();
        ScenarioNameTexts.Reset();
        MessengerProactiveTexts.Reset();
        ClarificationTexts.Reset();
        GracefulCorrectionTexts.Reset();
    }
}
