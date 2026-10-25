// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the recipe forcing spine of "dienst-aus-bestellung-schneiden": find_customer_candidates ->
/// create_shift (open order) -> seal_shift -> cut_shift. The resolver engages when a create intent and a split intent
/// are both present (skipping the customer lookup when a GUID is already supplied); the plan advances one step at a
/// time and only on the current step's success, captures the lone customer and injects it into create_shift, captures
/// the created order and injects it into seal_shift, and ends at a held seal_shift waiting for the user's confirmation
/// (a refused seal ends the forcing). The turn that confirms the seal resumes the plan on the confirmed replay of that
/// seal and forces cut_shift on the plannable shift the seal created.
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
    private const string OrderId = "33333333-3333-3333-3333-333333333333";
    private const string PlannableShiftId = "44444444-4444-4444-4444-444444444444";
    private const string OtherOrderId = "55555555-5555-5555-5555-555555555555";
    private const string Request =
        "Erstelle eine 24/7-Bestellung fuer Kunde Weiss und schneide sie in 3 Dienste auf: 07:00-15:00, 15:00-23:00, 23:00-07:00.";

    [Test]
    public void Resolve_Without_Guid_Engages_Starting_At_Customer_Lookup()
    {
        var plan = RecipeForcingResolver.Resolve(Request);

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

    [Test]
    public void GuaranteedSkillNames_Name_All_Four_Step_Skills_In_Order()
    {
        Assert.That(RecipeForcingResolver.GuaranteedSkillNames(Request), Is.EqualTo(new[]
        {
            RecipeConstants.FindCustomerSkill,
            RecipeConstants.CreateShiftSkill,
            RecipeConstants.SealShiftSkill,
            RecipeConstants.CutShiftSkill,
        }));
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
        Assert.That(plan.IsDeactivated, Is.True);
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

        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true, result: CreatedOrderResult(OrderId))]);
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.SealShiftSkill));
    }

    [Test]
    public void Plan_Captures_The_Created_Order_And_Injects_It_Into_Seal()
    {
        var plan = NewKnownCustomerPlan();

        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true, result: CreatedOrderResult(OrderId))]);

        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.SealShiftSkill));
        Assert.That(plan.CurrentStepNote, Is.EqualTo(RecipeConstants.SealShiftStepNote));
        Assert.That(plan.GetParameterInjections(RecipeConstants.SealShiftSkill),
            Does.ContainKey(RecipeConstants.ShiftIdParam).WithValue((object)OrderId));
        Assert.That(plan.GetParameterInjections(RecipeConstants.CreateShiftSkill), Is.Empty);
    }

    [Test]
    public void Plan_Stays_On_A_Held_Seal_And_Awaits_The_Confirmation()
    {
        var plan = PlanAtSeal();

        plan.Observe([HeldCall(RecipeConstants.SealShiftSkill)]);

        Assert.That(plan.IsActive, Is.True);
        Assert.That(plan.AwaitsConfirmation, Is.True);
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.SealShiftSkill));
        Assert.That(plan.IsStoppedAtSeal, Is.False);
    }

    [Test]
    public void Plan_Ends_When_The_Seal_Is_Refused()
    {
        var plan = PlanAtSeal();

        plan.Observe([Call(RecipeConstants.SealShiftSkill, success: false, result: "Cannot seal order 'X' yet - missing: at least one group.")]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.IsStoppedAtSeal, Is.True);
        Assert.That(plan.IsDeactivated, Is.False, "a refused seal is no ambiguous customer match");
        Assert.That(plan.CurrentSkill, Is.Null);
    }

    [Test]
    public void Plan_Captures_The_Plannable_Shift_Of_A_Seal_That_Ran_And_Injects_It_Into_Cut()
    {
        var plan = PlanAtSeal();

        plan.Observe([Call(RecipeConstants.SealShiftSkill, success: true, result: SealResult(OrderId, PlannableShiftId))]);

        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CutShiftSkill));
        Assert.That(plan.GetParameterInjections(RecipeConstants.CutShiftSkill),
            Does.ContainKey(RecipeConstants.ShiftIdParam).WithValue((object)PlannableShiftId));
    }

    [Test]
    public void Plan_Completes_After_Whole_Chain_Succeeds()
    {
        var plan = NewFullPlan();

        plan.Observe([Call(RecipeConstants.FindCustomerSkill, success: true, result: CustomerResult(Guid1))]);
        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true, result: CreatedOrderResult(OrderId))]);
        plan.Observe([Call(RecipeConstants.SealShiftSkill, success: true, result: SealResult(OrderId, PlannableShiftId))]);
        plan.Observe([Call(RecipeConstants.CutShiftSkill, success: true)]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.CurrentSkill, Is.Null);
        Assert.That(plan.CurrentStepNote, Is.Null);
    }

    [Test]
    public void SealContinuation_Waits_On_The_Confirmed_Replay_Then_Forces_Cut_On_The_Plannable_Shift()
    {
        var plan = RecipeForcingResolver.ResumeAfterSealConfirmation(Request, OrderId);

        Assert.That(plan.Name, Is.EqualTo(RecipeConstants.CutFromOrderRecipeName));
        Assert.That(plan.CurrentSkill, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(plan.GetParameterInjections(AutonomyDefaults.ConfirmPendingActionSkillName), Is.Empty);

        plan.Observe([ReplayCall(RecipeConstants.SealShiftSkill, success: true, result: SealResult(OrderId, PlannableShiftId))]);

        Assert.That(plan.IsActive, Is.True);
        Assert.That(plan.CurrentSkill, Is.EqualTo(RecipeConstants.CutShiftSkill));
        Assert.That(plan.GetParameterInjections(RecipeConstants.CutShiftSkill),
            Does.ContainKey(RecipeConstants.ShiftIdParam).WithValue((object)PlannableShiftId));
        Assert.That(plan.CurrentStepNote, Does.Contain(Request), "the parts live only in the request that started the flow");
    }

    [Test]
    public void SealContinuation_Ignores_An_Iteration_Without_The_Replay()
    {
        var plan = RecipeForcingResolver.ResumeAfterSealConfirmation(Request, OrderId);

        plan.Observe([Call("search_shifts", success: true)]);

        Assert.That(plan.IsActive, Is.True);
        Assert.That(plan.CurrentSkill, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
    }

    [Test]
    public void SealContinuation_Ends_When_The_Confirmed_Replay_Fails()
    {
        var plan = RecipeForcingResolver.ResumeAfterSealConfirmation(Request, OrderId);

        plan.Observe([ReplayCall(RecipeConstants.SealShiftSkill, success: false, result: "The confirmed order changed.")]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.IsStoppedAtSeal, Is.True);
    }

    [Test]
    public void SealContinuation_Ends_When_The_Replay_Sealed_Another_Order()
    {
        var plan = RecipeForcingResolver.ResumeAfterSealConfirmation(Request, OrderId);

        plan.Observe([ReplayCall(RecipeConstants.SealShiftSkill, success: true, result: SealResult(OtherOrderId, PlannableShiftId))]);

        Assert.That(plan.IsActive, Is.False);
        Assert.That(plan.IsStoppedAtSeal, Is.True);
    }

    [Test]
    public void SealContinuation_Ends_When_The_Replay_Ran_Another_Skill()
    {
        var plan = RecipeForcingResolver.ResumeAfterSealConfirmation(Request, OrderId);

        plan.Observe([ReplayCall("delete_shift", success: true, result: "Deleted.")]);

        Assert.That(plan.IsActive, Is.False);
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

    private static RecipeForcingPlan NewFullPlan() => RecipeForcingResolver.Resolve(Request)!;

    private static RecipeForcingPlan NewKnownCustomerPlan() =>
        RecipeForcingResolver.Resolve($"Erstelle eine 24/7-Bestellung fuer clientId={Guid1} und schneide sie in 3 Dienste auf.")!;

    private static RecipeForcingPlan PlanAtSeal()
    {
        var plan = NewKnownCustomerPlan();
        plan.Observe([Call(RecipeConstants.CreateShiftSkill, success: true, result: CreatedOrderResult(OrderId))]);
        return plan;
    }

    private static LLMFunctionCall Call(string name, bool success, string? result = null) =>
        new() { FunctionName = name, Success = success, Result = result };

    private static LLMFunctionCall HeldCall(string name) =>
        new() { FunctionName = name, Success = false, RequiresConfirmation = true, Result = "User confirmation required." };

    private static LLMFunctionCall ReplayCall(string replayedSkill, bool success, string result) =>
        new()
        {
            FunctionName = AutonomyDefaults.ConfirmPendingActionSkillName,
            ReplayedSkillName = success ? replayedSkill : null,
            Success = success,
            Result = result
        };

    private static string CustomerResult(string id) =>
        $"Found 1 customer(s). ASK THE USER which customer.\nData: {{\"Count\":1,\"Customers\":[{{\"CustomerId\":\"{id}\",\"Name\":\"Weiss\",\"City\":\"Bern\"}}]}}";

    private static string TwoCustomerResult(string id1, string id2) =>
        $"Found 2 customer(s).\nData: {{\"Count\":2,\"Customers\":[{{\"CustomerId\":\"{id1}\",\"Name\":\"A\",\"City\":\"X\"}},{{\"CustomerId\":\"{id2}\",\"Name\":\"B\",\"City\":\"Y\"}}]}}";

    private static string ZeroCustomerResult() =>
        "No customers found.\nData: {\"Count\":0,\"Customers\":[]}";

    private static string CreatedOrderResult(string orderId) =>
        $"Order 'X' created as an open order.\nData: {{\"ShiftId\":\"{orderId}\",\"Name\":\"X\"}}";

    private static string SealResult(string orderId, string plannableShiftId) =>
        $"Order 'X' sealed.\nData: {{\"OrderId\":\"{orderId}\",\"PlannableShiftId\":\"{plannableShiftId}\",\"Name\":\"X\",\"Verified\":true}}";
}
