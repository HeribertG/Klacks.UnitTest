// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The multi-part gate of the skill-graph association: a clause or sentence mark followed by more text opens it, in
/// Latin, CJK and Arabic script alike; a single question, also one ending in its own mark, keeps it shut.
/// </summary>

using Klacks.Api.Application.Services.Assistant.SkillGraph;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillAssociationGateTests
{
    [TestCase("Lisa ist morgen krank, trag das ein und schau, wer einspringen kann")]
    [TestCase("Herr Brunner hört auf. Was hängt daran?")]
    [TestCase("Close the month, but first show me the open errors")]
    [TestCase("明日リサは病気です。代わりを探して")]
    [TestCase("周六谁有空？然后安排第一个人")]
    [TestCase("ليزا مريضة غدا، ابحث عن بديل")]
    public void AMessageWithSeveralParts_OpensTheGate(string message)
    {
        SkillAssociationGate.LooksMultiPart(message).ShouldBeTrue();
    }

    [TestCase("Ist der Sonntag schon voll besetzt?")]
    [TestCase("I want to lock tomorrow's plan")]
    [TestCase("Trag Peters Urlaub ein.")]
    [TestCase("明日の計画をロックしたい。")]
    [TestCase("  ")]
    [TestCase(null)]
    public void ASinglePartMessage_KeepsTheGateShut(string? message)
    {
        SkillAssociationGate.LooksMultiPart(message).ShouldBeFalse();
    }
}
