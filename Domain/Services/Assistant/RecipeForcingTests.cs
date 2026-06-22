// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the recipe forcing spine: the resolver engages the "dienst-aus-bestellung-schneiden"
/// recipe when a create intent and a split intent are both present (skipping the customer lookup when a
/// GUID is already supplied); the plan advances one step at a time and only on the current step's
/// success, deterministically captures the lone matching customer from find_customer_candidates and
/// injects that clientId into create_shift, and deactivates when the customer is ambiguous.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeForcingTests
{
    private const string Guid1 = "11111111-1111-1111-1111-111111111111";
    private const string Guid2 = "22222222-2222-2222-2222-222222222222";

    [Test]
    public void Resolve_Without_Guid_Engages_Starting_At_Customer_Lookup()
    {
        var plan = RecipeForcingResolver.Resolve(
            "Erstelle eine 24/7-Bestellung fuer Kunde Weiss und schneide sie in 3 Dienste auf.");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.IsActive, Is.True);
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.FindCustomerSkill));
    }

    [Test]
    public void Resolve_With_Guid_Skips_Lookup_And_Injects_ClientId()
    {
        var plan = RecipeForcingResolver.Resolve(
            $"Erstelle eine 24/7-Bestellung fuer clientId={Guid1} und schneide sie in 3 Dienste auf.");

        Assert.That(plan, Is.Not.Null);
        Assert.That(plan!.CurrentSkill, Is.EqualTo(RecipeConstants.CreateShiftSkill));
        Assert.That(plan.GetParameterInjections(RecipeConstants.CreateShiftSkill),
            Does.ContainKey(RecipeConstants.ClientIdParam).WithValue((object)Guid1));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Zeige mir die Bestellung 11111111-1111-1111-1111-111111111111")] // GUID, no create/split intent
    [TestCase("Erstelle einen neuen Kunden 11111111-1111-1111-1111-111111111111")] // create + GUID, no split intent
    public void Resolve_Stays_Silent_When_Intents_Incomplete(string? message)
    {
        Assert.That(RecipeForcingResolver.Resolve(message), Is.Null);
    }

    [Test]
    public void Plan_Captures_Lone_Customer_Then_Advances_And_Injects()
    {
        var plan = NewFullPlan();
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.FindCustomerSkill));

        plan.Observe([Call(RecipeConstants.FindCustomerSkill, success: true, result: CustomerResult(Guid1))]);

        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CreateShiftSkill));
        Assert.That(plan.GetParameterInjections(RecipeConstants.CreateShiftSkill),
            Does.ContainKey(RecipeConstants.ClientIdParam).WithValue((object)Guid1));
    }

    [Test]
    public void Plan_Deactivates_On_Ambiguous_Customer()
    {
        var plan = NewFullPlan();

        plan.Observe([Call(RecipeConstants.FindCustomerSkill, success: true, result: TwoCustomerResult(Guid1, Guid2))]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.CurrentSkill, Is.Null);
    }

    [Test]
    public void Plan_Deactivates_When_No_Customer_Matches()
    {
        var plan = NewFullPlan();

        plan.Observe([Call(RecipeConstants.FindCustomerSkill, success: true, result: ZeroCustomerResult())]);

        Assert.That(plan.IsActive, Is.False);
    }

    [Test]
    public void Plan_Advances_Only_On_Successful_Current_Step()
    {
        var plan = NewKnownCustomerPlan();
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CreateShiftSkill));

        plan.Observe([Call(RecipeConstants.CutShiftSkill, success: true)]); // wrong skill
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CreateShiftSkill));

        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: false)]); // current skill failed
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CreateShiftSkill));

        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true)]);
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CutShiftSkill));
    }

    [Test]
    public void Plan_Completes_After_Whole_Chain_Succeeds()
    {
        var plan = NewFullPlan();

        plan.Observe([Call(RecipeConstants.FindCustomerSkill, success: true, result: CustomerResult(Guid1))]);
        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true)]);
        plan.Observe([Call(RecipeConstants.CutShiftSkill, success: true)]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.CurrentSkill, Is.Null);
        Assert.That(plan.CurrentStepNote, Is.Null);
    }

    [TestCase("Mach mir einen geteilten 24-Stunden-Dienst fuer Spitex Bern")]
    [TestCase("Leg einen 24h-Dienst an und teile ihn in drei Schichten")]
    [TestCase("Richte einen Rund-um-die-Uhr-Dienst ein und schneide ihn in drei Teile")]
    [TestCase("Trag einen geteilten Nachtdienst fuer Spitex ein")]
    [TestCase("Plane einen 24-Stunden-Einsatz und schneide ihn")]
    [TestCase("Bestellung anlegen und in Tag- und Nachtschicht aufteilen")]
    [TestCase("Erstell mir bitte einen aufgeteilten Wochenenddienst")]
    public void Resolve_Engages_On_Natural_Create_And_Split_Requests(string message)
    {
        Assert.That(RecipeForcingResolver.Resolve(message), Is.Not.Null);
    }

    [TestCase("Kannst du mir mitteilen, wie ich eine neue Schicht erstelle?")] // how-to; "mitteilen" is not a split
    [TestCase("Wie erstelle ich einen geteilten 24h-Dienst?")] // how-to question
    [TestCase("Kannst du die Pflege-Dienste aufteilen?")] // "Pflege" must not match the create stem "leg"
    [TestCase("Ich will die neue Pflegekraft einteilen")] // "einteilen" is not a split
    [TestCase("Gibt es Vorteile, wenn ich neue Dienste erstelle?")] // info question; "Vorteile" is not a split
    [TestCase("Aktualisiere den Drehplan und erstelle die neue Woche")] // edit of an existing thing
    [TestCase("Wie lege ich einen geteilten Dienst an?")] // how-to question
    [TestCase("Zeig mir die Pflegeliste und teile sie mit dem Team")] // display request, not create-and-cut
    public void Resolve_Stays_Silent_On_NonRequests(string message)
    {
        Assert.That(RecipeForcingResolver.Resolve(message), Is.Null);
    }

    private static RecipeForcingPlan NewFullPlan() => new(RecipeConstants.CutFromOrderRecipeName, Steps());

    private static RecipeForcingPlan NewKnownCustomerPlan() =>
        new(RecipeConstants.CutFromOrderRecipeName, Steps(), startIndex: 1, initialClientId: Guid1);

    private static RecipeForcingStep[] Steps() =>
    [
        new(RecipeConstants.FindCustomerSkill, RecipeConstants.FindCustomerStepNote, CapturesCustomer: true),
        new(RecipeConstants.CreateShiftSkill, RecipeConstants.CreateShiftStepNote, NeedsCustomerId: true),
        new(RecipeConstants.CutShiftSkill, RecipeConstants.CutShiftStepNote),
    ];

    private static LLMFunctionCall Call(string name, bool success, string? result = null) =>
        new() { FunctionName = name, Success = success, Result = result };

    private static string CustomerResult(string id) =>
        $"Found 1 customer(s). ASK THE USER which customer.\nData: {{\"Count\":1,\"Customers\":[{{\"CustomerId\":\"{id}\",\"Name\":\"Weiss\",\"City\":\"Bern\"}}]}}";

    private static string TwoCustomerResult(string id1, string id2) =>
        $"Found 2 customer(s).\nData: {{\"Count\":2,\"Customers\":[{{\"CustomerId\":\"{id1}\",\"Name\":\"A\",\"City\":\"X\"}},{{\"CustomerId\":\"{id2}\",\"Name\":\"B\",\"City\":\"Y\"}}]}}";

    private static string ZeroCustomerResult() =>
        "No customers found.\nData: {\"Count\":0,\"Customers\":[]}";
}
