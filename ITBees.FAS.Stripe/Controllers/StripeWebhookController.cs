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

namespace ITBees.FAS.Stripe.Controllers
{
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
            _logger.LogDebug("Received stripe webhook request");
            _logger.LogDebug("json parsed : \n{Json}\n\nParse event...", json);

            var stripeEvent = ParseEvent(json, Request.Headers["Stripe-Signature"]);
            _paymentDbLoggerService.Log(new PaymentOperatorLog()
            {
                Event = stripeEvent.Type,
                Received = DateTime.Now,
                Operator = "Stripe webhook",
                JsonEvent = json
            });

            if (stripeEvent.Type == "checkout.session.completed")
            {
                _logger.LogDebug("Event checkout.session.completed");
                var session = stripeEvent.Data.Object as Session;

                // NOTE: In Basil / Stripe.net v48 the property is `Subscription` (string), not `SubscriptionId`.
                _logger.LogDebug("Closing successfulPayment...");
                _paymentSessionCreator.CloseSuccessfulPayment(
                    Guid.Parse(session.ClientReferenceId),
                    session.Created,
                    session.Subscription.Id,
                    stripeEvent.Id);
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

                var stripeSubscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId;

                var invoiceData = await ApplySubscriptionPlanAndCreateInvoiceForRenewal(
                    invoice.CustomerEmail,
                    invoice.Created,
                    stripeSubscriptionId);

                var stripeEventId = stripeEvent.Id;

                _logger.LogDebug("Creating payment session for subscription renewal...");
                var paymentSessionFromSubscriptionRenew =
                    _paymentSessionCreator.CreatePaymentSessionFromSubscriptionRenew(
                        invoice.Created,
                        invoiceData.CreatedByGuid,
                        _paymentProcessor,
                        invoiceData.Guid,
                        _paymentProcessor.ProcessorName,
                        stripeEventId,
                        null,
                        stripeSubscriptionId,
                        invoiceData.InvoiceRequested);

                _logger.LogDebug("Created renewal payment session {PaymentSessionGuid}",
                    paymentSessionFromSubscriptionRenew.Guid);

                return Ok();
            }

            if (stripeEvent.Type == "charge.refunded" ||
                stripeEvent.Type == "charge.refund.updated" ||
                stripeEvent.Type.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Event {EventType}", stripeEvent.Type);

                // Handle both payload shapes
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
            return new RequestOptions
            {
                ApiKey = _platformSettingsService.GetSetting("StripeSecretKey")
            };
        }

        private async Task<InvoiceDataVm> ApplySubscriptionPlanAndCreateInvoiceForRenewal(
            string customerEmail,
            DateTime startingFrom,
            string stripeSubscriptionId = null)
        {
            try
            {
                Company company = null;
                PlatformSubscriptionPlan platformSubscriptionPlan = null;

                // Try mapping from subscription id if present
                company = _paymentSessionCreator
                    .TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(stripeSubscriptionId);

                if (company == null)
                {
                    _logger.LogInformation("Processing subscription renewal for email: {Email}", customerEmail);

                    var user = _userAccountRoRepo.GetData(x => x.Email == customerEmail, x => x.LastUsedCompany)
                        .FirstOrDefault();

                    if (user == null)
                    {
                        _logger.LogError("User not found for email: {Email}", customerEmail);
                        throw new Exception($"User not found for email: {customerEmail}");
                    }

                    if (user.LastUsedCompany.CompanyPlatformSubscription?.SubscriptionPlanGuid == null)
                    {
                        _logger.LogError("No active subscription plan for company: {Company}",
                            user.LastUsedCompany.CompanyName);
                        throw new Exception("No active subscription plan for company: " +
                                            user.LastUsedCompany.CompanyName);
                    }

                    company = user.LastUsedCompany;
                    platformSubscriptionPlan = _platformSubscriptionPlanRoRepo.GetFirst(x =>
                        x.Guid == user.LastUsedCompany.CompanyPlatformSubscription.SubscriptionPlanGuid);
                }
                else
                {
                    platformSubscriptionPlan = company.CompanyPlatformSubscription.SubscriptionPlan;
                }

                if (company == null || platformSubscriptionPlan == null)
                {
                    _logger.LogError("Subscription plan not found for {Email}", customerEmail);
                    throw new Exception($"Subscription plan not found for company of {customerEmail}");
                }

                _logger.LogInformation("Extending subscription for client: {Email}, plan: {Plan}",
                    customerEmail, platformSubscriptionPlan.PlanName);

                // Apply extension starting from the invoice creation moment
                _applySubscriptionPlanToCompanyService.Apply(platformSubscriptionPlan, company.Guid, startingFrom);

                // Create new invoice data based on last invoice for this company/plan
                var invoiceData = _invoiceDataService.CreateNewInvoiceBasedOnLastInvoice(company, platformSubscriptionPlan);

                _logger.LogInformation(
                    "Successfully processed subscription renewal for company: {Company}, Stripe subscription id: {SubId}",
                    company.CompanyName, stripeSubscriptionId);

                return invoiceData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error in ApplySubscriptionPlanAndCreateInvoiceForRenewal for email: {Email}, Stripe subscription id: {SubId}",
                    customerEmail, stripeSubscriptionId);
                throw;
            }
        }

        private Event ParseEvent(string json, string stripeSignatureHeader)
        {
            try
            {
                return EventUtility.ConstructEvent(
                    json,
                    stripeSignatureHeader,
                    _webhookSecret,
                    tolerance: 300,
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
                var req = CreateStripeRequestOptions();
                Charge charge = null;
                PaymentIntent pi = null;
                Invoice invoice = null;
                string subscriptionId = null;
                Customer customer = null;

                // Try to get Charge
                if (!string.IsNullOrEmpty(refund.ChargeId))
                    charge = await new ChargeService().GetAsync(refund.ChargeId, options: null, requestOptions: req);

                // Try to get PaymentIntent
                var paymentIntentId = refund.PaymentIntentId ?? charge?.PaymentIntentId;
                if (!string.IsNullOrEmpty(paymentIntentId))
                    pi = await new PaymentIntentService().GetAsync(paymentIntentId, options: null, requestOptions: req);

                // In Basil, link PI -> Invoice via InvoicePayments
                if (!string.IsNullOrEmpty(pi?.Id))
                {
                    invoice = await TryFindInvoiceByPaymentIntentAsync(pi.Id, req);
                    subscriptionId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
                }

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
                if (!string.IsNullOrEmpty(subscriptionId))
                {
                    company = _paymentSessionCreator
                        .TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(subscriptionId);
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
                    Event = $"Refund {(isFull ? "FULL" : "PARTIAL")} {refundAmount} {currency} " +
                            $"for PI={pi?.Id}, Charge={charge?.Id}, Invoice={invoice?.Id}, " +
                            $"Subscription={subscriptionId}, Customer={customer?.Id}, " +
                            $"Company={(company != null ? company.Guid.ToString() : "unknown")}",
                    JsonEvent = $"refund_id={refund.Id}"
                });

                if (company != null && isFull)
                {
                    // Business rule: on full refund revoke the subscription access and issue corrective invoice
                    _applySubscriptionPlanToCompanyService.Revoke(company.Guid);

                    if (string.IsNullOrEmpty(subscriptionId))
                    {
                        _invoiceDataService.CreateCorrectiveInvoiceForRefundForLastPaymentSession(company.Guid);
                    }
                    else
                    {
                        _invoiceDataService.CreateCorrectiveInvoiceForRefund(
                            company.Guid,
                            refundAmount / 100.0m,
                            subscriptionId);
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
                string subscriptionId = null;
                Customer customer = null;

                if (!string.IsNullOrEmpty(charge.PaymentIntentId))
                    pi = await new PaymentIntentService().GetAsync(charge.PaymentIntentId, options: null, requestOptions: req);

                if (!string.IsNullOrEmpty(pi?.Id))
                {
                    invoice = await TryFindInvoiceByPaymentIntentAsync(pi.Id, req);
                    subscriptionId = invoice?.Parent?.SubscriptionDetails?.SubscriptionId;
                }

                if (!string.IsNullOrEmpty(charge.CustomerId))
                    customer = await new CustomerService().GetAsync(charge.CustomerId, options: null, requestOptions: req);

                var refundedAmount = charge.AmountRefunded;
                var currency = charge.Currency;
                var captured = charge.AmountCaptured > 0 ? charge.AmountCaptured : charge.Amount;
                var isFull = refundedAmount >= captured;

                Company company = null;
                if (!string.IsNullOrEmpty(subscriptionId))
                {
                    company = _paymentSessionCreator
                        .TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(subscriptionId);
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
                    Event = $"Charge refunded {(isFull ? "FULL" : "PARTIAL")} {refundedAmount} {currency} " +
                            $"for PI={pi?.Id}, Charge={charge?.Id}, Invoice={invoice?.Id}, " +
                            $"Subscription={subscriptionId}, Customer={customer?.Id}, " +
                            $"Company={(company != null ? company.Guid.ToString() : "unknown")}",
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

        /// <summary>
        /// Finds the Invoice linked to a given PaymentIntent using the InvoicePayments API (Basil).
        /// </summary>
        private async Task<Invoice> TryFindInvoiceByPaymentIntentAsync(string paymentIntentId, RequestOptions req)
        {
            try
            {
                // NOTE: In Basil, link PI -> Invoice via InvoicePayments: filter by payment[payment_intent]
                var ipService = new InvoicePaymentService();
                var listOptions = new InvoicePaymentListOptions
                {
                    Limit = 1,
                    Payment = new InvoicePaymentPaymentOptions()
                    {
                        PaymentIntent = paymentIntentId
                    }
                };

                var ipList = await ipService.ListAsync(listOptions, requestOptions: req);

                var invoiceId = ipList?.Data?.FirstOrDefault()?.Invoice.Id;
                if (string.IsNullOrEmpty(invoiceId))
                    return null;

                var invoice = await new InvoiceService().GetAsync(invoiceId, options: null, requestOptions: req);
                return invoice;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not find Invoice via InvoicePayments for PaymentIntent {PI}", paymentIntentId);
                return null;
            }
        }
    }
}
