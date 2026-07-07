// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeCancellationDetector — verifies it fires on short explicit aborts across
/// core and plugin languages and, crucially (precision bias), stays silent on longer messages so
/// a recipe slot answer that happens to contain a stop-like word is never misread as an abort.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeCancellationDetectorTests
{
    [OneTimeSetUp]
    public void ConfigurePluginEntries()
    {
        RecipeCancellationDetector.Configure(["anuluj", "キャンセル", "取消"]);
    }

    [TestCase("abbrechen")]
    [TestCase("Abbrechen bitte")]
    [TestCase("brich ab")]
    [TestCase("vergiss es")]
    [TestCase("nein, doch nicht")]
    [TestCase("cancel")]
    [TestCase("never mind")]
    [TestCase("forget it")]
    [TestCase("annule")]
    [TestCase("laisse tomber")]
    [TestCase("annulla")]
    [TestCase("lascia stare")]
    [TestCase("anuluj")]
    [TestCase("キャンセル")]
    [TestCase("取消")]
    public void IsCancellation_True_For_Explicit_Aborts(string message)
    {
        RecipeCancellationDetector.IsCancellation(message).ShouldBeTrue(message);
    }

    [TestCase("Max Müller")]
    [TestCase("die Gruppe Nord")]
    [TestCase("ab dem 1. August")]
    [TestCase("Team Reinigung Ost")]
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsCancellation_False_For_Slot_Answers(string? message)
    {
        RecipeCancellationDetector.IsCancellation(message).ShouldBeFalse(message ?? "<null>");
    }

    [Test]
    public void IsCancellation_False_For_Long_Message_Containing_StopWord()
    {
        RecipeCancellationDetector.IsCancellation(
                "der Mitarbeiter heisst Stop und arbeitet in der Gruppe Nord seit August")
            .ShouldBeFalse("long messages are slot answers, not aborts");
    }
}
