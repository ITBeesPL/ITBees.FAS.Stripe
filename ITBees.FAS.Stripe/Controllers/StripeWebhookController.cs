using ITBees.FAS.Payments.Interfaces;
using ITBees.Interfaces.Platforms;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace ITBees.FAS.Stripe.Controllers;

public class StripeWebhookController : RestfulControllerBase<StripeWebhookController>
{
    private readonly IPaymentSessionCreator _paymentSessionCreator;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly string _webhookSecret;

    public StripeWebhookController(ILogger<StripeWebhookController> logger, 
        IPaymentSessionCreator paymentSessionCreator,
        IPlatformSettingsService platformSettingsService) : base(logger)
    {
        _paymentSessionCreator = paymentSessionCreator;
        _platformSettingsService = platformSettingsService;
        _webhookSecret = platformSettingsService.GetSetting("StripeWebhookKey");
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeEvent = ParseEvent(json, Request.Headers["Stripe-Signature"]);

        if (stripeEvent.Type == Events.CheckoutSessionCompleted)
        {
            var session = stripeEvent.Data.Object as Session;
            _paymentSessionCreator.CloseSuccessfulPayment(Guid.Parse(session.ClientReferenceId));
        }

        return Ok();
    }

    private Event ParseEvent(string json, string stripeSignatureHeader)
    {
        try
        {
            return EventUtility.ConstructEvent(json, stripeSignatureHeader, _webhookSecret);
        }
        catch (StripeException e)
        {
            Response.StatusCode = 400;
            throw new Exception("Webhook verification failed", e);
        }
    }

}