// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PlanTriggerHeuristic: the deterministic truth table (multiple mutation targets AND
/// no recipe match) plus multilingual (de/en/fr/it) segment counting via the existing
/// MutationIntentDetector, so no new language-sensitive phrase list is introduced.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PlanTriggerHeuristicTests
{
    // ── Truth table ───────────────────────────────────────────────────────────

    [Test]
    public void RecipeMatched_IsNeverACandidate()
    {
        PlanTriggerHeuristic.IsPlanCandidate(
            "Erstelle einen Kunden, lege einen Mitarbeiter an", recipeMatched: true).ShouldBeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void EmptyMessage_IsNeverACandidate(string? message)
    {
        PlanTriggerHeuristic.IsPlanCandidate(message, recipeMatched: false).ShouldBeFalse();
    }

    [Test]
    public void SingleMutation_NoRecipe_IsNotACandidate()
    {
        PlanTriggerHeuristic.IsPlanCandidate("Erstelle einen Kunden Müller", recipeMatched: false).ShouldBeFalse();
    }

    [Test]
    public void MultipleMutations_NoRecipe_IsACandidate()
    {
        PlanTriggerHeuristic.IsPlanCandidate(
            "Erstelle einen Kunden, lege einen Mitarbeiter an", recipeMatched: false).ShouldBeTrue();
    }

    [Test]
    public void PureQuestion_IsNotACandidate()
    {
        PlanTriggerHeuristic.IsPlanCandidate(
            "Wie erstelle ich einen Kunden und wie lege ich einen Mitarbeiter an?", recipeMatched: false)
            .ShouldBeFalse();
    }

    // ── Multilingual segment counting ─────────────────────────────────────────

    [TestCase("Erstelle einen Kunden, lege einen Mitarbeiter an und plane einen Dienst.", 2)]
    [TestCase("Create a customer, add an employee and assign him to a group.", 2)]
    [TestCase("Crée un client, ajoute un employé.", 2)]
    [TestCase("Crea un cliente, aggiungi un dipendente.", 2)]
    public void CountMutationSegments_CountsPerClauseMutations(string message, int minExpected)
    {
        PlanTriggerHeuristic.CountMutationSegments(message).ShouldBeGreaterThanOrEqualTo(minExpected);
    }

    [TestCase("Erstelle einen Kunden, lege einen Mitarbeiter an.")]
    [TestCase("Create a customer, add an employee.")]
    [TestCase("Crée un client, ajoute un employé.")]
    [TestCase("Crea un cliente, aggiungi un dipendente.")]
    public void MultilingualMultiMutation_IsACandidate(string message)
    {
        PlanTriggerHeuristic.IsPlanCandidate(message, recipeMatched: false).ShouldBeTrue();
    }

    [Test]
    public void SingleSegmentWithoutPunctuation_CountsOnce()
    {
        // Language-neutral segmentation on punctuation only: two verbs joined by "und" in one clause
        // are one segment, so this is not a candidate (documented precision trade-off).
        PlanTriggerHeuristic.CountMutationSegments("Erstelle einen Kunden und plane einen Dienst")
            .ShouldBe(1);
    }
}
