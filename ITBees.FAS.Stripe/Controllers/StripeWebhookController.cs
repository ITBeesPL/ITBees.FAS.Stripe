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
    private readonly IReadOnlyRepository<Company> _companyRoRepo;
    private readonly IReadOnlyRepository<UserAccount> _userAccountRoRepo;
    private readonly IApplySubscriptionPlanToCompanyService _applySubscriptionPlanToCompanyService;
    private readonly IReadOnlyRepository<PlatformSubscriptionPlan> _platformSubscriptionPlanRoRepo;
    private readonly IInvoiceDataService _invoiceDataService;

    public StripeWebhookController(ILogger<StripeWebhookController> logger,
        IPaymentSessionCreator paymentSessionCreator,
        IPlatformSettingsService platformSettingsService,
        IPaymentDbLoggerService paymentDbLoggerService,
        IReadOnlyRepository<Company> companyRoRepo,
        IReadOnlyRepository<UserAccount> userAccountRoRepo,
        IApplySubscriptionPlanToCompanyService applySubscriptionPlanToCompanyService,
        IReadOnlyRepository<PlatformSubscriptionPlan> platformSubscriptionPlanRoRepo,
        IInvoiceDataService invoiceDataService) : base(logger)
    {
        _logger = logger;
        _paymentSessionCreator = paymentSessionCreator;
        _platformSettingsService = platformSettingsService;
        _paymentDbLoggerService = paymentDbLoggerService;
        _webhookSecret = platformSettingsService.GetSetting("StripeWebhookKey");
        _companyRoRepo = companyRoRepo;
        _userAccountRoRepo = userAccountRoRepo;
        _applySubscriptionPlanToCompanyService = applySubscriptionPlanToCompanyService;
        _platformSubscriptionPlanRoRepo = platformSubscriptionPlanRoRepo;
        _invoiceDataService = invoiceDataService;
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
        else if (stripeEvent.Type == "charge.succeeded")
        {
            _logger.LogDebug("Event charge.succeeded");
            var charge = stripeEvent.Data.Object as Charge;
            await HandleChargeSucceeded(charge);
        }
        else if (stripeEvent.Type == "customer.subscription.updated")
        {
            _logger.LogDebug("Event customer.subscription.updated");
            var subscription = stripeEvent.Data.Object as Subscription;
            await HandleSubscriptionUpdated(subscription);
        }

        return Ok();
    }

    private async Task HandleChargeSucceeded(Charge charge)
    {
        try
        {
            _logger.LogInformation($"Processing charge.succeeded for charge {charge.Id}");
            
            var customerEmail = charge.BillingDetails?.Email;
            if (string.IsNullOrEmpty(customerEmail))
            {
                _logger.LogWarning($"No billing email found in charge {charge.Id}");
                return;
            }

            await ProcessSubscriptionRenewal(customerEmail, charge.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing charge.succeeded for charge {charge.Id}");
        }
    }

    private async Task HandleSubscriptionUpdated(Subscription subscription)
    {
        try
        {
            _logger.LogInformation($"Processing customer.subscription.updated for subscription {subscription.Id}");
            
            var customerService = new CustomerService();
            var customer = await customerService.GetAsync(subscription.CustomerId);
            
            if (customer == null || string.IsNullOrEmpty(customer.Email))
            {
                _logger.LogWarning($"No customer email found for subscription {subscription.Id}");
                return;
            }

            await ProcessSubscriptionRenewal(customer.Email, subscription.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing customer.subscription.updated for subscription {subscription.Id}");
        }
    }

    private async Task ProcessSubscriptionRenewal(string customerEmail, string stripeEventId)
    {
        try
        {
            _logger.LogInformation($"Processing subscription renewal for email: {customerEmail}");

            var userAccount = _userAccountRoRepo.GetData(x => x.Email == customerEmail).FirstOrDefault();
            if (userAccount == null)
            {
                _logger.LogWarning($"User not found for email: {customerEmail}");
                return;
            }
            
            var company = _companyRoRepo.GetData(x => x.OwnerGuid == userAccount.Guid, 
                x => x.CompanyPlatformSubscription, 
                x => x.CompanyPlatformSubscription.SubscriptionPlan).FirstOrDefault();
            
            if (company == null)
            {
                _logger.LogWarning($"Company not found for user: {customerEmail}");
                return;
            }

            if (company.CompanyPlatformSubscription?.SubscriptionPlanGuid == null)
            {
                _logger.LogWarning($"No active subscription plan for company: {company.CompanyName}");
                return;
            }
            
            var subscriptionPlan = _platformSubscriptionPlanRoRepo.GetFirst(x => x.Guid == company.CompanyPlatformSubscription.SubscriptionPlanGuid);
            if (subscriptionPlan == null)
            {
                _logger.LogWarning($"Subscription plan not found for company: {company.CompanyName}");
                return;
            }

            _logger.LogInformation($"Extending subscription for company: {company.CompanyName}, plan: {subscriptionPlan.PlanName}");
            
            _applySubscriptionPlanToCompanyService.Apply(subscriptionPlan, company.Guid);
            
            var existingInvoiceData = _invoiceDataService.Get(company.Guid);
            
            var invoiceDataIm = new InvoiceDataIm
            {
                CompanyGuid = company.Guid,
                City = existingInvoiceData?.City ?? "",
                Country = existingInvoiceData?.Country ?? "",
                CompanyName = existingInvoiceData?.CompanyName ?? company.CompanyName,
                InvoiceEmail = existingInvoiceData?.InvoiceEmail ?? "",
                NIP = existingInvoiceData?.NIP ?? "",
                PostCode = existingInvoiceData?.PostCode ?? "",
                Street = existingInvoiceData?.Street ?? "",
                SubscriptionPlanGuid = subscriptionPlan.Guid,
                InvoiceRequested = existingInvoiceData?.InvoiceRequested ?? false
            };

            _invoiceDataService.Create(invoiceDataIm);

            _logger.LogInformation($"Successfully processed subscription renewal for company: {company.CompanyName}, Stripe event: {stripeEventId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in ProcessSubscriptionRenewal for email: {customerEmail}, Stripe event: {stripeEventId}");
        }
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