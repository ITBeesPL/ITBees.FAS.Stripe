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
using ITBees.FAS.Stripe.Services;

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
        private readonly IReadOnlyRepository<PaymentSession> _paymentSessionRoRepo;
        private readonly IReadOnlyRepository<Company> _companyRoRepo;
        private readonly ICompanyStripeSubscriptionService? _companyStripeSubscriptionService;
        private readonly IStripeSubscriptionLookupService? _stripeSubscriptionLookupService;

        public StripeWebhookController(
            ILogger<StripeWebhookController> logger,
            IPaymentSessionCreator paymentSessionCreator,
            IPlatformSettingsService platformSettingsService,
            IPaymentDbLoggerService paymentDbLoggerService,
            IReadOnlyRepository<UserAccount> userAccountRoRepo,
            IApplySubscriptionPlanToCompanyService applySubscriptionPlanToCompanyService,
            IReadOnlyRepository<PlatformSubscriptionPlan> platformSubscriptionPlanRoRepo,
            IInvoiceDataService invoiceDataService,
            IFasPaymentProcessor paymentProcessor,
            IReadOnlyRepository<PaymentSession> paymentSessionRoRepo,
            IReadOnlyRepository<Company> companyRoRepo,
            // Optional: registered by FasStripeSetup; without them replaced subscriptions are not cancelled
            // and a full refund always revokes the company's access (previous behaviour).
            ICompanyStripeSubscriptionService? companyStripeSubscriptionService = null,
            IStripeSubscriptionLookupService? stripeSubscriptionLookupService = null
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
            _paymentSessionRoRepo = paymentSessionRoRepo;
            _companyRoRepo = companyRoRepo;
            _companyStripeSubscriptionService = companyStripeSubscriptionService;
            _stripeSubscriptionLookupService = stripeSubscriptionLookupService;
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
                var paymentSessionGuid = Guid.Parse(session.ClientReferenceId);
                _paymentSessionCreator.CloseSuccessfulPayment(
                    paymentSessionGuid,
                    session.Created,
                    session.SubscriptionId,
                    stripeEvent.Id,
                    StripeMetadataKeys.GetGuid(session.Metadata, StripeMetadataKeys.SubscriptionPlanGuid));
                _logger.LogDebug("Closing successfulPayment - done.");

                if (session.Mode == "subscription" && string.IsNullOrEmpty(session.SubscriptionId) == false)
                {
                    await CancelReplacedSubscriptions(paymentSessionGuid, session.SubscriptionId);
                }

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

                var invoiceData = await ApplySubscriptionPlanAndCreateInvoiceForRenewal(invoice, stripeSubscriptionId);

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
            Invoice invoice,
            string stripeSubscriptionId = null)
        {
            var customerEmail = invoice.CustomerEmail;
            var startingFrom = invoice.Created;
            var subscriptionMetadata = invoice.Parent?.SubscriptionDetails?.Metadata;
            try
            {
                var company = GetRenewedCompany(subscriptionMetadata, stripeSubscriptionId, customerEmail);
                var platformSubscriptionPlan =
                    GetRenewedSubscriptionPlan(invoice, subscriptionMetadata, stripeSubscriptionId, company);

                if (company == null || platformSubscriptionPlan == null)
                {
                    _logger.LogError("Subscription plan not found for {Email}", customerEmail);
                    throw new Exception($"Subscription plan not found for company of {customerEmail}");
                }

                _logger.LogInformation("Extending subscription for client: {Email}, plan: {Plan}",
                    customerEmail, platformSubscriptionPlan.PlanName);

                // Apply extension starting from the invoice creation moment
                ApplyRenewedPlan(company, platformSubscriptionPlan, startingFrom, stripeSubscriptionId);

                // Invoice data snapshot bound to the plan Stripe really charged for - the invoice is issued from it.
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

        /// <summary>
        /// Company of a renewed subscription: metadata (subscriptions bought after it was introduced), the checkout
        /// payment session, and only for legacy data the last used company of the customer's e-mail.
        /// </summary>
        private Company GetRenewedCompany(IDictionary<string, string>? subscriptionMetadata,
            string stripeSubscriptionId, string customerEmail)
        {
            var metadataCompanyGuid =
                StripeMetadataKeys.GetGuid(subscriptionMetadata, StripeMetadataKeys.CompanyGuid);
            if (metadataCompanyGuid != null)
            {
                var metadataCompany = _companyRoRepo.GetData(x => x.Guid == metadataCompanyGuid.Value)
                    .FirstOrDefault();
                if (metadataCompany != null)
                    return metadataCompany;
            }

            var company = _paymentSessionCreator
                .TryGetCompanyWithSubscriptionPlanFromPaymentSubscriptionId(stripeSubscriptionId);
            if (company != null)
                return company;

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

            return user.LastUsedCompany;
        }

        /// <summary>
        /// Plan the renewal really charged for. It used to be the company's current plan, which is wrong whenever
        /// the company has another (parallel) subscription or an operator assigned a plan manually - e.g. a yearly
        /// subscription renewed and got invoiced and applied as the 3-month plan.
        /// </summary>
        private PlatformSubscriptionPlan GetRenewedSubscriptionPlan(Invoice invoice,
            IDictionary<string, string>? subscriptionMetadata, string stripeSubscriptionId, Company company)
        {
            var metadataPlanGuid =
                StripeMetadataKeys.GetGuid(subscriptionMetadata, StripeMetadataKeys.SubscriptionPlanGuid);
            if (metadataPlanGuid != null)
            {
                var metadataPlan = _platformSubscriptionPlanRoRepo.GetData(x => x.Guid == metadataPlanGuid.Value)
                    .FirstOrDefault();
                if (metadataPlan != null)
                    return metadataPlan;

                _logger.LogWarning("Plan {PlanGuid} from metadata of subscription {SubscriptionId} not found",
                    metadataPlanGuid, stripeSubscriptionId);
            }

            // Legacy subscription (plan not stored in Stripe metadata) - match by the charged amount and period.
            var companyPlanGuid = company?.CompanyPlatformSubscription?.SubscriptionPlanGuid;
            var companyCurrentPlan = companyPlanGuid == null
                ? null
                : _platformSubscriptionPlanRoRepo.GetData(x => x.Guid == companyPlanGuid.Value).FirstOrDefault();
            var checkoutPlan = string.IsNullOrEmpty(stripeSubscriptionId)
                ? null
                : _paymentSessionRoRepo.GetData(
                        x => x.OperatorTransactionId == stripeSubscriptionId && x.FromSubscriptionRenew == false,
                        x => x.InvoiceData, x => x.InvoiceData.SubscriptionPlan)
                    .OrderBy(x => x.Created)
                    .FirstOrDefault()?.InvoiceData?.SubscriptionPlan;

            var line = invoice.Lines?.Data?.FirstOrDefault(x => x.Parent?.SubscriptionItemDetails != null)
                       ?? invoice.Lines?.Data?.FirstOrDefault();
            if (line?.Period == null)
            {
                _logger.LogWarning("Renewal invoice {InvoiceId} has no billed line, using the company's current plan",
                    invoice.Id);
                return companyCurrentPlan ?? checkoutPlan;
            }

            var recurringPlans = _platformSubscriptionPlanRoRepo
                .GetData(x => x.IsTrial == false && x.IsOneTimePayment == false)
                .ToList();
            var billedPlan = StripeSubscriptionPlanMatcher.Match(line.Amount, line.Currency,
                plan => StripeSubscriptionPlanMatcher.MatchesPeriod(plan, line.Period.Start, line.Period.End),
                new[] { checkoutPlan, companyCurrentPlan },
                recurringPlans);

            if (billedPlan != null && billedPlan.Guid != companyCurrentPlan?.Guid)
            {
                var message =
                    $"Renewal of subscription {stripeSubscriptionId} charged {line.Amount / 100m} {line.Currency} " +
                    $"for {line.Period.Start:yyyy-MM-dd} - {line.Period.End:yyyy-MM-dd}, which is plan {billedPlan.PlanName}; " +
                    $"company {company?.CompanyName} has plan {companyCurrentPlan?.PlanName} assigned - using {billedPlan.PlanName}";
                _logger.LogWarning(message);
                _paymentDbLoggerService.Log(new PaymentOperatorLog
                {
                    Operator = "Stripe webhook",
                    Received = DateTime.Now,
                    Event = message,
                    JsonEvent = $"invoice_id={invoice.Id}"
                });
            }

            return billedPlan;
        }

        /// <summary>
        /// A renewal never shortens the access the company already has - with a parallel subscription or after an
        /// operator's manual extension the renewed period may end earlier than the current one.
        /// </summary>
        private void ApplyRenewedPlan(Company company, PlatformSubscriptionPlan platformSubscriptionPlan,
            DateTime startingFrom, string stripeSubscriptionId)
        {
            // Same formula as IApplySubscriptionPlanToCompanyService.Apply.
            var renewedActiveTo = startingFrom
                .AddMonths(platformSubscriptionPlan.Interval)
                .AddDays(platformSubscriptionPlan.IntervalDays);
            var currentSubscription = company.CompanyPlatformSubscription;

            if (currentSubscription?.SubscriptionActiveTo != null &&
                currentSubscription.SubscriptionActiveTo > renewedActiveTo)
            {
                var message =
                    $"Renewal of subscription {stripeSubscriptionId} ({platformSubscriptionPlan.PlanName} to {renewedActiveTo:yyyy-MM-dd}) " +
                    $"would shorten plan {currentSubscription.SubscriptionPlanName} of company {company.CompanyName} " +
                    $"active to {currentSubscription.SubscriptionActiveTo:yyyy-MM-dd} - company subscription left unchanged";
                if (currentSubscription.SubscriptionPlanGuid == platformSubscriptionPlan.Guid)
                {
                    _logger.LogInformation(message);
                    return;
                }

                _logger.LogWarning(message + " (parallel subscription?)");
                _paymentDbLoggerService.Log(new PaymentOperatorLog
                {
                    Operator = "Stripe webhook",
                    Received = DateTime.Now,
                    Event = message + " (parallel subscription?)",
                    JsonEvent = $"subscription_id={stripeSubscriptionId}"
                });
                return;
            }

            _applySubscriptionPlanToCompanyService.Apply(platformSubscriptionPlan, company.Guid, startingFrom);
        }

        /// <summary>
        /// A company that bought a new recurring plan must not keep paying for the old one. Never throws -
        /// the checkout itself is already closed at this point.
        /// </summary>
        private async Task CancelReplacedSubscriptions(Guid paymentSessionGuid, string newSubscriptionId)
        {
            try
            {
                if (_companyStripeSubscriptionService == null)
                {
                    _logger.LogWarning(
                        "ICompanyStripeSubscriptionService is not registered - subscriptions replaced by {SubscriptionId} are not cancelled",
                        newSubscriptionId);
                    return;
                }

                var paymentSession = _paymentSessionRoRepo
                    .GetData(x => x.Guid == paymentSessionGuid, x => x.InvoiceData)
                    .FirstOrDefault();
                var companyGuid = paymentSession?.InvoiceData?.CompanyGuid;
                if (companyGuid == null || paymentSession!.OrderPackGuid != null)
                    return;

                await _companyStripeSubscriptionService.CancelReplacedSubscriptions(companyGuid.Value,
                    newSubscriptionId);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Cancelling subscriptions replaced by {SubscriptionId} failed", newSubscriptionId);
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
                    await RevokeUnlessOtherSubscriptionBills(company, subscriptionId, refund.Id);

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
                    await RevokeUnlessOtherSubscriptionBills(company, subscriptionId, charge.Id);
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
        /// A full refund revokes the company's access - unless the refunded payment belongs to one of several
        /// parallel subscriptions (e.g. refunding the duplicate charge of a plan change): the company still pays
        /// through the other one, so it must keep its plan.
        /// </summary>
        private async Task RevokeUnlessOtherSubscriptionBills(Company company, string? refundedSubscriptionId,
            string refundReference)
        {
            if (_stripeSubscriptionLookupService != null && string.IsNullOrEmpty(refundedSubscriptionId) == false)
            {
                var otherSubscriptionIds =
                    (await _stripeSubscriptionLookupService.GetCompanySubscriptionIdsAsync(company.Guid))
                    .Where(x => x != refundedSubscriptionId);
                foreach (var otherSubscriptionId in otherSubscriptionIds)
                {
                    var otherSubscription =
                        await _stripeSubscriptionLookupService.GetSubscriptionAsync(otherSubscriptionId);
                    if (otherSubscription == null || _stripeSubscriptionLookupService.IsBilling(otherSubscription) == false)
                        continue;

                    var message =
                        $"Full refund {refundReference} of subscription {refundedSubscriptionId} - access of company " +
                        $"{company.CompanyName} NOT revoked, it still pays through subscription {otherSubscriptionId}";
                    _logger.LogWarning(message);
                    _paymentDbLoggerService.Log(new PaymentOperatorLog
                    {
                        Operator = "Stripe webhook",
                        Received = DateTime.Now,
                        Event = message,
                        JsonEvent = $"company_guid={company.Guid}"
                    });
                    return;
                }
            }

            _applySubscriptionPlanToCompanyService.Revoke(company.Guid);
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
