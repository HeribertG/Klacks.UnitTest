// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Installs the conversation signals of the core languages and all shipped language packs into every static detector and
/// the collision holder through ConversationSignalsPluginLoader.Install, the same wiring Load runs at startup, and takes
/// them out again. The detectors are process-wide static state that Configure only ever adds to, so every fixture that
/// installs the packs must call Uninstall in its teardown or it decides the outcome of every fixture that runs after it.
/// Uninstall keeps the core refusal vocabulary, which the assembly installs once for every fixture.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.TestHelpers;

internal static class ShippedConversationSignals
{
    public static void Install()
    {
        var baseDirectory = RepositoryRootLocator.ApiProject;
        ConversationSignalsPluginLoader.Install(
            ConversationSignalsPluginLoader.ReadCore(baseDirectory),
            ConversationSignalsPluginLoader.ReadPacks(baseDirectory));
    }

    public static void Uninstall()
    {
        ConversationSignalCollisions.Reset();
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
        ImplicitCorrectionDetector.Reset();
        RecipeCancellationDetector.Reset();
        RefusalSignalDetector.Reset();
    }
}
