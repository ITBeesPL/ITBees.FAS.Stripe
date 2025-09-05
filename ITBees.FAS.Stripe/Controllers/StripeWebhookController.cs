using ITBees.FAS.Payments.Interfaces;
using ITBees.FAS.Payments.Interfaces.Models;
using ITBees.FAS.Payments.Controllers.Models;
using ITBees.Interfaces.Platforms;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using ITBees.Interfaces.Repository;
using ITBees.Models.Companies;
using ITBees.Models.Users;
using ITBees.Models.Payments;

namespace ITBees.FAS.Stripe.Controllers;

public class StripeWebhookController : RestfulControllerBase<StripeWebhookController>
{
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IPaymentSessionCreator _paymentSessionCreator;
    private readonly IPlatformSettingsService _platformSettingsService;
    private readonly IPaymentDbLoggerService _paymentDbLoggerService;
    private readonly string _webhookSecret;
    private readonly IReadOnlyRepository<UserAccount> _userAccountRoRepo;
    private readonly IApplySubscriptionPlanToCompanyService _applySubscriptionPlanToCompanyService;
    private readonly IReadOnlyRepository<PlatformSubscriptionPlan> _platformSubscriptionPlanRoRepo;
    private readonly IInvoiceDataService _invoiceDataService;
    private readonly IFasPaymentProcessor _paymentProcessor;

    public StripeWebhookController(
        ILogger<StripeWebhookController> logger,
        IPaymentSessionCreator paymentSessionCreator,
        IPlatformSettingsService platformSettingsService,
        IPaymentDbLoggerService paymentDbLoggerService,
        IReadOnlyRepository<UserAccount> userAccountRoRepo,
        IApplySubscriptionPlanToCompanyService applySubscriptionPlanToCompanyService,
        IReadOnlyRepository<PlatformSubscriptionPlan> platformSubscriptionPlanRoRepo,
        IInvoiceDataService invoiceDataService,
        IFasPaymentProcessor paymentProcessor
    ) : base(logger)
    {
        _logger = logger;
        _paymentSessionCreator = paymentSessionCreator;
        _platformSettingsService = platformSettingsService;
        _paymentDbLoggerService = paymentDbLoggerService;
        _webhookSecret = platformSettingsService.GetSetting("StripeWebhookKey");
        _userAccountRoRepo = userAccountRoRepo;
        _applySubscriptionPlanToCompanyService = applySubscriptionPlanToCompanyService;
        _platformSubscriptionPlanRoRepo = platformSubscriptionPlanRoRepo;
        _invoiceDataService = invoiceDataService;
        _paymentProcessor = paymentProcessor;
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        _logger.LogDebug($"Received stripe webhook request");
        _logger.LogDebug($"json parsed : \n" + json + "\n\nParse event...");
        var stripeEvent = ParseEvent(json, Request.Headers["Stripe-Signature"]);
        _paymentDbLoggerService.Log(new PaymentOperatorLog()
            { Event = stripeEvent.Type, Received = DateTime.Now, Operator = "Stripe webhook", JsonEvent = json });

        if (stripeEvent.Type == "checkout.session.completed")
        {
            _logger.LogDebug("Event checkout.session.completed");
            var session = stripeEvent.Data.Object as Session;

            _logger.LogDebug("Closing successfulPayment...");
            _paymentSessionCreator.CloseSuccessfulPayment(Guid.Parse(session.ClientReferenceId), session.Created,
                session.SubscriptionId);
            _logger.LogDebug("Closing successfulPayment - done.");
            return Ok();
        }

        if (stripeEvent.Type == "invoice.payment_succeeded")
        {
            _logger.LogDebug("Event invoice.payment_succeeded");
            var invoice = stripeEvent.Data.Object as Invoice;
            if (invoice.BillingReason == "subscription_create")
            {
                _logger.LogDebug("Skipping invoice.payment_succeeded for first subscription_create event.");
                return Ok();
            }

            var invoiceData =
                await ApplySubscriptionPlanAndCreateInvoiceForRenewal(invoice.CustomerEmail, invoice.Created,
                    invoice.SubscriptionId);
            _logger.LogDebug("Creating payment session for subscription renewal...");
            var paymentSessionFromSubscriptionRenew = _paymentSessionCreator.CreatePaymentSessionFromSubscriptionRenew(
                invoice.Created, null,
                _paymentProcessor, invoiceData.Guid, _paymentProcessor.ProcessorName, null, invoice.SubscriptionId,
                invoiceData.InvoiceRequested);
            _logger.LogDebug(
                "Creating payment session for subscription renewal - new paymentSession guid : {paymentSessionFromSubscriptionRenew.Guid}");
        }

        if (stripeEvent.Type == "charge.refunded" ||
            stripeEvent.Type == "charge.refund.updated" ||
            stripeEvent.Type.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug($"Event {stripeEvent.Type}");

            // Handle both payload shapes gracefully
            if (stripeEvent.Data.Object is Refund refundObj)
            {
                await HandleRefundAsync(refundObj);
            }
            else if (stripeEvent.Data.Object is Charge chargeObj)
            {
                await HandleChargeRefundedAsync(chargeObj);
            }
            else
            {
                _logger.LogWarning("Refund event payload type not recognized: {Type}",
                    stripeEvent.Data.Object?.GetType().FullName);
            }

            return Ok();
        }

        return Ok();
    }

    private RequestOptions CreateStripeRequestOptions()
    {
        return new RequestOptions { ApiKey = _platformSettingsService.GetSetting("StripeSecretKey") };
    }

    private async Task<InvoiceDataVm> ApplySubscriptionPlanAndCreateInvoiceForRenewal(string customerEmail,
        DateTime startingFrom,
        string? stripeSubscriptionId = null)
    {
        try
        {
            Company? company = null;
            PlatformSubscriptionPlan? platformSubscriptionPlan = null;
            company =
                _paymentSessionCreator.TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(stripeSubscriptionId);
            if (company == null)
            {
                _logger.LogInformation($"Processing subscription renewal for email: {customerEmail}");

                var user = _userAccountRoRepo.GetData(x => x.Email == customerEmail, x => x.LastUsedCompany)
                    .FirstOrDefault();
                if (user == null)
                {
                    _logger.LogError($"User not found for email: {customerEmail}");
                    throw new Exception($"User not found for email: {customerEmail}");
                }

                if (user.LastUsedCompany.CompanyPlatformSubscription?.SubscriptionPlanGuid == null)
                {
                    _logger.LogError($"No active subscription plan for company: {user.LastUsedCompany.CompanyName}");
                    throw new Exception("No active subscription plan for company: " + user.LastUsedCompany.CompanyName);
                }

                company = user.LastUsedCompany;

                platformSubscriptionPlan = _platformSubscriptionPlanRoRepo.GetFirst(x =>
                    x.Guid == user.LastUsedCompany.CompanyPlatformSubscription.SubscriptionPlanGuid);
            }
            else
            {
                platformSubscriptionPlan = company.CompanyPlatformSubscription.SubscriptionPlan;
            }

            if (company == null)
            {
                _logger.LogError($"Subscription plan not found for company: {customerEmail}");
                throw new Exception($"Subscription plan not found for company: {customerEmail}");
            }

            _logger.LogInformation(
                $"Extending subscription for client: {customerEmail}, plan: {platformSubscriptionPlan.PlanName}");

            _applySubscriptionPlanToCompanyService.Apply(platformSubscriptionPlan, company.Guid, startingFrom);

            var invoiceData = _invoiceDataService.CreateNewInvoiceBasedOnLastInvoice(company, platformSubscriptionPlan);

            _logger.LogInformation(
                $"Successfully processed subscription renewal for company: {company.CompanyName}, Stripe subscription id : {stripeSubscriptionId}");

            return invoiceData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                $"Error in ApplySubscriptionPlanAndCreateInvoiceForRenewal for email: {customerEmail}, Stripe subscription id : {stripeSubscriptionId}");
            throw;
        }
    }

    private Event ParseEvent(string json, string stripeSignatureHeader)
    {
        try
        {
            return EventUtility.ConstructEvent(json, stripeSignatureHeader, _webhookSecret, tolerance: 300,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException e)
        {
            _paymentDbLoggerService.Log(new PaymentOperatorLog()
            {
                JsonEvent = json,
                Operator = "Stripe webhook",
                Received = DateTime.Now,
                Event = $"Webhook error ! {e.Message}",
            });
            Response.StatusCode = 400;
            _logger.LogError(e, json);
            throw new Exception("Webhook verification failed", e);
        }
        catch (Exception e)
        {
            _paymentDbLoggerService.Log(new PaymentOperatorLog()
            {
                JsonEvent = json,
                Operator = "Stripe webhook",
                Received = DateTime.Now,
                Event = $"Webhook error ! {e.Message}",
            });
            Response.StatusCode = 400;
            _logger.LogError(e, json);
            throw new Exception("Webhook failed", e);
        }
    }

    private async Task HandleRefundAsync(Refund refund)
    {
        try
        {
            // Gather as much correlation info as possible
            var req = CreateStripeRequestOptions();
            Charge charge = null;
            PaymentIntent pi = null;
            Invoice invoice = null;
            Subscription subscription = null;
            Customer customer = null;

            // Try to get Charge
            if (!string.IsNullOrEmpty(refund.ChargeId))
                charge = await new ChargeService().GetAsync(refund.ChargeId, options: null, requestOptions: req);

            // Try to get PaymentIntent
            var paymentIntentId = refund.PaymentIntentId ?? charge?.PaymentIntentId;
            if (!string.IsNullOrEmpty(paymentIntentId))
                pi = await new PaymentIntentService().GetAsync(paymentIntentId, options: null, requestOptions: req);

            // Try to get Invoice (via PI)
            if (!string.IsNullOrEmpty(pi?.InvoiceId))
                invoice = await new InvoiceService().GetAsync(pi.InvoiceId, options: null, requestOptions: req);

            // Try to get Subscription (via Invoice)
            if (!string.IsNullOrEmpty(invoice?.SubscriptionId))
                subscription =
                    await new SubscriptionService().GetAsync(invoice.SubscriptionId, options: null,
                        requestOptions: req);

            // Try to get Customer (prefer PI.CustomerId; fallback to charge.CustomerId)
            var customerId = pi?.CustomerId ?? charge?.CustomerId;
            if (!string.IsNullOrEmpty(customerId))
                customer = await new CustomerService().GetAsync(customerId, options: null, requestOptions: req);

            // Determine full vs partial refund
            var refundAmount = refund.Amount; // minor units
            var currency = refund.Currency;
            bool isFull = false;
            if (charge != null)
            {
                var captured = charge.AmountCaptured > 0 ? charge.AmountCaptured : charge.Amount;
                isFull = charge.AmountRefunded >= captured;
            }

            Company company = null;
            if (!string.IsNullOrEmpty(subscription?.Id))
            {
                company =
                    _paymentSessionCreator.TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(subscription.Id);
            }

            if (company == null && !string.IsNullOrEmpty(customer?.Email))
            {
                var user = _userAccountRoRepo.GetData(x => x.Email == customer.Email, x => x.LastUsedCompany)
                    .FirstOrDefault();
                if (user != null) company = user.LastUsedCompany;
            }

            _paymentDbLoggerService.Log(new PaymentOperatorLog
            {
                Operator = "Stripe webhook",
                Received = DateTime.Now,
                Event =
                    $"Refund {(isFull ? "FULL" : "PARTIAL")} {refundAmount} {currency} for PI={pi?.Id}, Charge={charge?.Id}, Invoice={invoice?.Id}, Subscription={subscription?.Id}, Customer={customer?.Id}, Company={(company != null ? company.Guid.ToString() : "unknown")}",
                JsonEvent = $"refund_id={refund.Id}"
            });

            if (company != null && isFull)
            {
                _applySubscriptionPlanToCompanyService.Revoke(company.Guid);
                if (string.IsNullOrEmpty(subscription?.Id))
                {
                    _invoiceDataService.CreateCorrectiveInvoiceForRefundForLastPaymentSession(company.Guid);
                }
                else
                {
                    _invoiceDataService.CreateCorrectiveInvoiceForRefund(company.Guid, refundAmount / 100.0m,
                        subscription.Id);
                }
            }

            _logger.LogInformation("Handled refund: refund={RefundId}, isFull={IsFull}, company={CompanyGuid}",
                refund.Id, isFull, company?.Guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while handling refund (refund object).");
        }
    }

    private async Task HandleChargeRefundedAsync(Charge charge)
    {
        try
        {
            var req = CreateStripeRequestOptions();
            PaymentIntent pi = null;
            Invoice invoice = null;
            Subscription subscription = null;
            Customer customer = null;

            if (!string.IsNullOrEmpty(charge.PaymentIntentId))
                pi = await new PaymentIntentService().GetAsync(charge.PaymentIntentId, options: null,
                    requestOptions: req);
            if (!string.IsNullOrEmpty(pi?.InvoiceId))
                invoice = await new InvoiceService().GetAsync(pi.InvoiceId, options: null, requestOptions: req);
            if (!string.IsNullOrEmpty(invoice?.SubscriptionId))
                subscription =
                    await new SubscriptionService().GetAsync(invoice.SubscriptionId, options: null,
                        requestOptions: req);
            if (!string.IsNullOrEmpty(charge.CustomerId))
                customer = await new CustomerService().GetAsync(charge.CustomerId, options: null, requestOptions: req);

            // Charge provides refunded amount directly
            var refundedAmount = charge.AmountRefunded;
            var currency = charge.Currency;
            var captured = charge.AmountCaptured > 0 ? charge.AmountCaptured : charge.Amount;
            var isFull = refundedAmount >= captured;

            Company company = null;
            if (!string.IsNullOrEmpty(subscription?.Id))
            {
                company =
                    _paymentSessionCreator.TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(subscription.Id);
            }

            if (company == null && !string.IsNullOrEmpty(customer?.Email))
            {
                var user = _userAccountRoRepo.GetData(x => x.Email == customer.Email, x => x.LastUsedCompany)
                    .FirstOrDefault();
                if (user != null) company = user.LastUsedCompany;
            }

            _paymentDbLoggerService.Log(new PaymentOperatorLog
            {
                Operator = "Stripe webhook",
                Received = DateTime.Now,
                Event =
                    $"Charge refunded {(isFull ? "FULL" : "PARTIAL")} {refundedAmount} {currency} for PI={pi?.Id}, Charge={charge?.Id}, Invoice={invoice?.Id}, Subscription={subscription?.Id}, Customer={customer?.Id}, Company={(company != null ? company.Guid.ToString() : "unknown")}",
                JsonEvent = $"charge_id={charge.Id}"
            });

            if (company != null && isFull)
            {
                _applySubscriptionPlanToCompanyService.Revoke(company.Guid);
                _invoiceDataService.CreateCorrectiveInvoiceForRefundForLastPaymentSession(company.Guid);
            }

            _logger.LogInformation("Handled charge.refunded: charge={ChargeId}, isFull={IsFull}, company={CompanyGuid}",
                charge.Id, isFull, company?.Guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while handling charge.refunded.");
        }
    }
}