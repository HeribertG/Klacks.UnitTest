// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateDonationCheckoutSkill, the only skill that starts a real payment flow and is
/// therefore classified Sensitive with the explicit note that it "must not be fail-open". Every rejected
/// case is asserted to leave the self-API untouched, so a regression that lets an out-of-range amount or
/// an unsupported currency through would fail here instead of reaching Stripe. The boundaries themselves
/// (exactly MinAmount, exactly MaxAmount) are pinned, the default and normalisation of the currency are
/// checked against the body the endpoint actually receives, and a rejection from the endpoint has to
/// arrive at the caller with its concrete cause instead of the client's generic fallback text.
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Donations;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateDonationCheckoutSkillTests
{
    private const string DonationRoute = "api/backend/Donation/checkout-session";
    private const string SkillName = "create_donation_checkout";
    private const string CheckoutUrl = "https://checkout.stripe.com/c/pay/cs_test_a1b2c3";
    private const string CallerToken = "caller-jwt";
    private const string NotConfigured = "Donation checkout is not configured.";
    private const string GenericClientFallback = "rejected as invalid";
    private const decimal ValidAmount = 25m;

    private FakeSelfApi _api = null!;
    private CreateDonationCheckoutSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, DonationRoute, new CreateDonationCheckoutResponse { Url = CheckoutUrl });
        _skill = new CreateDonationCheckoutSkill(_api.Client);
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task AMissingAmount_ThrowsTheDocumentedArgumentException_AndSendsNothing()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));

        exception.Message.ShouldContain("amount");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task AnAmountBelowTheMinimum_IsRejectedWithoutEverCallingTheEndpoint()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(DonationCheckoutLimits.MinAmount - 0.01m));

        _api.Calls.ShouldBeEmpty();
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("outside that range");
    }

    [Test]
    public async Task AnAmountAboveTheMaximum_IsRejectedWithoutEverCallingTheEndpoint()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(DonationCheckoutLimits.MaxAmount + 0.01m));

        _api.Calls.ShouldBeEmpty();
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("outside that range");
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ZeroAndNegativeAmounts_AreRejectedWithoutEverCallingTheEndpoint(decimal amount)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(amount));

        _api.Calls.ShouldBeEmpty();
        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task ExactlyTheMinimumAmount_IsAccepted_AndReachesTheEndpointUnchanged()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(DonationCheckoutLimits.MinAmount));

        result.Success.ShouldBeTrue(result.Message);
        _api.SingleCall.Route.ShouldBe(DonationRoute);
        _api.BodyOf<CreateDonationCheckoutRequest>()!.Amount.ShouldBe(DonationCheckoutLimits.MinAmount);
    }

    [Test]
    public async Task ExactlyTheMaximumAmount_IsAccepted_EvenWhenItArrivesAsAToolCallJsonNumber()
    {
        var jsonAmount = JsonSerializer.Deserialize<JsonElement>(
            DonationCheckoutLimits.MaxAmount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["amount"] = jsonAmount });

        result.Success.ShouldBeTrue(result.Message);
        _api.BodyOf<CreateDonationCheckoutRequest>()!.Amount.ShouldBe(DonationCheckoutLimits.MaxAmount);
    }

    [Test]
    public async Task AnUnsupportedCurrency_IsRejectedWithoutEverCallingTheEndpoint()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount, "USD"));

        _api.Calls.ShouldBeEmpty();
        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("USD");
        result.Message.ShouldContain(DonationCheckoutLimits.CurrencyChf);
        result.Message.ShouldContain(DonationCheckoutLimits.CurrencyEur);
    }

    [Test]
    public async Task AnOmittedCurrency_SendsTheDefaultChfToTheEndpoint()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount));

        result.Success.ShouldBeTrue(result.Message);
        _api.BodyOf<CreateDonationCheckoutRequest>()!.Currency.ShouldBe(DonationCheckoutLimits.DefaultCurrency);
        DonationCheckoutLimits.DefaultCurrency.ShouldBe(DonationCheckoutLimits.CurrencyChf);
    }

    [Test]
    public async Task ALowercaseCurrency_IsNormalisedToUppercaseBeforeItIsSent()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount, " eur "));

        result.Success.ShouldBeTrue(result.Message);
        _api.BodyOf<CreateDonationCheckoutRequest>()!.Currency.ShouldBe(DonationCheckoutLimits.CurrencyEur);
    }

    [Test]
    public async Task ABlankCurrency_IsTreatedAsOmittedAndFallsBackToTheDefault()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount, "   "));

        result.Success.ShouldBeTrue(result.Message);
        _api.BodyOf<CreateDonationCheckoutRequest>()!.Currency.ShouldBe(DonationCheckoutLimits.DefaultCurrency);
    }

    [Test]
    public async Task TheCheckoutUrlComesBackUnchanged_InBothTheDataAndTheMessage()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount, "EUR"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain(CheckoutUrl);
        JsonSerializer.Serialize(result.Data).ShouldContain(CheckoutUrl);
    }

    [Test]
    public async Task TheSelfCallRepresentsTheCallersTokenAndNamesTheSkill()
    {
        await _skill.ExecuteAsync(Ctx(), Params(ValidAmount));

        _api.SingleCall.BearerToken.ShouldBe(CallerToken);
        _api.SingleCall.SkillName.ShouldBe(SkillName);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }

    [Test]
    public async Task ARejectionFromTheEndpoint_KeepsItsConcreteCauseInsteadOfAGenericMessage()
    {
        _api.Respond(
            HttpMethod.Post,
            DonationRoute,
            new { message = NotConfigured, detail = NotConfigured },
            HttpStatusCode.BadRequest);

        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(NotConfigured);
        result.Message!.ShouldNotContain(GenericClientFallback);
    }

    [Test]
    public async Task AnAcceptedCheckoutWithoutAPaymentLink_IsReportedAsAFailure()
    {
        _api.Respond(HttpMethod.Post, DonationRoute, new CreateDonationCheckoutResponse { Url = null });

        var result = await _skill.ExecuteAsync(Ctx(), Params(ValidAmount));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("without a payment link");
    }

    [Test]
    public async Task WithoutACallerToken_TheRequestIsRefusedBeforeItIsSent()
    {
        var result = await _skill.ExecuteAsync(Ctx() with { AccessToken = null }, Params(ValidAmount));

        result.Success.ShouldBeFalse();
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task AnUnparseableAmount_IsReportedAsMissingRatherThanAsInvalid()
    {
        var exception = await Should.ThrowAsync<ArgumentException>(
            async () => await _skill.ExecuteAsync(
                Ctx(), new Dictionary<string, object> { ["amount"] = "not-a-number" }));

        exception.Message.ShouldContain("is missing");
        _api.Calls.ShouldBeEmpty();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        AccessToken = new BearerToken(CallerToken)
    };

    private static Dictionary<string, object> Params(decimal amount, string? currency = null)
    {
        var parameters = new Dictionary<string, object> { ["amount"] = amount };
        if (currency is not null)
        {
            parameters["currency"] = currency;
        }

        return parameters;
    }
}
