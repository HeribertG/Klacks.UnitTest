// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Installs the core-language conversation-signal vocabulary once for the whole test assembly. The
/// detectors no longer compile that vocabulary in - it is read from conversation-signals-core.json at
/// application startup, which no unit test runs, so without this installer every core-language
/// expectation would fail. Only the core file is loaded, never the language packs: plugin entries are
/// additive, process-wide static state and would change the outcome of the fixtures that configure
/// their own. A module initializer and not an assembly-level SetUpFixture: a SetUpFixture declared
/// outside any namespace is the root node of the whole assembly, so every --filter whose name fragment
/// matched it selected all tests below it and turned a filtered run into the full suite.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Application.Klacksy;

internal static class CoreConversationSignalsInstaller
{
    [ModuleInitializer]
    internal static void LoadCoreConversationSignals()
    {
        ConversationSignalsPluginLoader.LoadCore(AppContext.BaseDirectory);
    }
}
