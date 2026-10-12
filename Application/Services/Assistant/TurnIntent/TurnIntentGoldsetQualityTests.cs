// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Quality gate of the turn-intent labelset (turn-intent-v1.json): the file parses, ids are unique, a template never
/// carries two labels (so a split by template cannot leak), every minimal pair holds exactly one Ask and one Do per
/// locale, labels stay in {Ask, Do, Other}, no text is damaged, and no text equals a turn-selection-v1 message.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.TurnIntent;

[TestFixture]
public class TurnIntentGoldsetQualityTests
{
    private const char ReplacementCharacter = '�';

    private TurnIntentGoldsetDocument _document = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _document = TurnIntentGoldsetReader.Load();
    }

    [Test]
    public void TheFile_ParsesIsOfTheExpectedKindAndIsNotEmpty()
    {
        _document.Kind.ShouldBe(TurnIntentGoldsetReader.ExpectedKind);
        _document.Items.ShouldNotBeEmpty();
    }

    [Test]
    public void EveryItem_HasAllRequiredFields()
    {
        var violations = _document.Items
            .Where(item => string.IsNullOrWhiteSpace(item.Id)
                           || string.IsNullOrWhiteSpace(item.TemplateId)
                           || string.IsNullOrWhiteSpace(item.Locale)
                           || string.IsNullOrWhiteSpace(item.Text)
                           || string.IsNullOrWhiteSpace(item.Kind))
            .Select(item => item.Id)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryId_IsUnique()
    {
        var duplicates = _document.Items
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        duplicates.ShouldBeEmpty();
    }

    [Test]
    public void EveryLabel_IsAskDoOrOther()
    {
        var violations = _document.Items
            .Where(item => !TurnIntentLabels.All.Contains(item.Label, StringComparer.Ordinal))
            .Select(item => $"{item.Id}: '{item.Label}'")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryTemplate_CarriesExactlyOneLabel()
    {
        var violations = _document.Items
            .GroupBy(item => item.TemplateId, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryPairInEveryLocale_HoldsExactlyOneAskAndOneDo()
    {
        var violations = _document.Items
            .Where(item => item.PairId != null)
            .GroupBy(item => (item.PairId, item.Locale))
            .Where(group => !group.Select(item => item.Label)
                .OrderBy(label => label, StringComparer.Ordinal)
                .SequenceEqual([TurnIntentLabels.Ask, TurnIntentLabels.Do]))
            .Select(group => $"{group.Key.PairId}/{group.Key.Locale}: [{string.Join(", ", group.Select(item => item.Label))}]")
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void NoText_ContainsAReplacementCharacter()
    {
        var violations = _document.Items
            .Where(item => item.Text.Contains(ReplacementCharacter))
            .Select(item => item.Id)
            .ToList();

        violations.ShouldBeEmpty();
    }

    [Test]
    public void NoText_EqualsATurnSelectionMessage()
    {
        var turnSelectionMessages = TurnIntentGoldsetReader.LoadTurnSelectionMessages();

        var violations = _document.Items
            .Where(item => turnSelectionMessages.Contains(TurnIntentGoldsetReader.Normalize(item.Text)))
            .Select(item => $"{item.Id}: {item.Text}")
            .ToList();

        violations.ShouldBeEmpty();
    }
}
