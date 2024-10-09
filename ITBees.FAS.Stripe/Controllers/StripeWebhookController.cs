using ITBees.FAS.Payments.Interfaces;
using ITBees.FAS.Payments.Interfaces.Models;
using ITBees.Interfaces.Platforms;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace ITBees.FAS.Stripe.Controllers;

public class StripeWebhookController : RestfulControllerBase<StripeWebhookController>
{
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IPaymentSessionCreator _paymentSessionCreator;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IPaymentDbLoggerService _paymentDbLoggerService;
    private readonly string _webhookSecret;

    public StripeWebhookController(ILogger<StripeWebhookController> logger,
        IPaymentSessionCreator paymentSessionCreator,
        IPlatformSettingsService platformSettingsService,
        IPaymentDbLoggerService paymentDbLoggerService) : base(logger)
    {
        _logger = logger;
        _paymentSessionCreator = paymentSessionCreator;
        _platformSettingsService = platformSettingsService;
        _paymentDbLoggerService = paymentDbLoggerService;
        _webhookSecret = platformSettingsService.GetSetting("StripeWebhookKey");
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        _logger.LogDebug($"Received stripe webhook request");
        _logger.LogDebug($"json parsed : \n" + json +"\n\nParse event...");
        var stripeEvent = ParseEvent(json, Request.Headers["Stripe-Signature"]);
        _logger.LogDebug("parse event finished");
        _paymentDbLoggerService.Log(new PaymentOperatorLog() { Event = stripeEvent.Type, Received = DateTime.Now, Operator = "Stripe webhook", JsonEvent = json });
        if (stripeEvent.Type == "checkout.session.completed")
        {
            _logger.LogDebug("Event checkout sesion completed");
            var session = stripeEvent.Data.Object as Session;
            _logger.LogDebug("Closing successfulPayment...");
            _paymentSessionCreator.CloseSuccessfulPayment(Guid.Parse(session.ClientReferenceId));
            _logger.LogDebug("Closing successfulPayment - done.");
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
            _paymentDbLoggerService.Log(new PaymentOperatorLog()
            {
                JsonEvent = json,
                Operator = "Stripe webhook",
                Received = DateTime.Now,
                Event = $"Webhook error ! {e.Message}"
            });
            Response.StatusCode = 400;
            _logger.LogError(e, json);
            throw new Exception("Webhook verification failed", e);
        }
    }

}