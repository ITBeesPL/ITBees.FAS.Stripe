using ITBees.FAS.Payments.Interfaces.Models;
using ITBees.Interfaces.Platforms;
using ITBees.Interfaces.Repository;
using ITBees.Models.Payments;
using Microsoft.Extensions.Logging;
using Stripe;

namespace ITBees.FAS.Stripe.Services;

public class StripeSubscriptionLookupService : IStripeSubscriptionLookupService
{
    private static readonly HashSet<string> BillingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "trialing", "past_due", "unpaid", "incomplete", "paused"
    };

    private readonly IReadOnlyRepository<PaymentSession> _paymentSessionRoRepo;
    private readonly IReadOnlyRepository<PlatformSubscriptionPlan> _platformSubscriptionPlanRoRepo;
    private readonly ILogger<StripeSubscriptionLookupService> _logger;
    private readonly Lazy<StripeClient> _stripeClient;
    private List<PlatformSubscriptionPlan>? _recurringPlans;

    public StripeSubscriptionLookupService(
        IPlatformSettingsService platformSettingsService,
        IReadOnlyRepository<PaymentSession> paymentSessionRoRepo,
        IReadOnlyRepository<PlatformSubscriptionPlan> platformSubscriptionPlanRoRepo,
        ILogger<StripeSubscriptionLookupService> logger)
    {
        _paymentSessionRoRepo = paymentSessionRoRepo;
        _platformSubscriptionPlanRoRepo = platformSubscriptionPlanRoRepo;
        _logger = logger;
        _stripeClient = new Lazy<StripeClient>(() =>
            new StripeClient(platformSettingsService.GetSetting("StripeSecretKey")));
    }

    public StripeClient StripeClient => _stripeClient.Value;

    public bool IsBilling(Subscription subscription)
    {
        return BillingStatuses.Contains(subscription.Status);
    }

    public async Task<List<string>> GetCompanySubscriptionIdsAsync(Guid companyGuid)
    {
        var subscriptionIds = _paymentSessionRoRepo.GetData(x =>
                x.InvoiceData.CompanyGuid == companyGuid
                && x.FromSubscriptionRenew == false
                && x.OrderPackGuid == null
                && x.Success
                && x.OperatorTransactionId != null
                && x.OperatorTransactionId.StartsWith("sub_"))
            .Select(x => x.OperatorTransactionId!)
            .ToList();

        try
        {
            // Search is eventually consistent (about a minute), which is fine for subscriptions bought earlier.
            var searchResult = await new SubscriptionService(StripeClient).SearchAsync(new SubscriptionSearchOptions
            {
                Query = $"metadata['{StripeMetadataKeys.CompanyGuid}']:'{companyGuid}'",
                Limit = 100
            });
            subscriptionIds.AddRange(searchResult.Data.Select(x => x.Id));
        }
        catch (StripeException e)
        {
            _logger.LogWarning(e, "Stripe subscription search by company {CompanyGuid} failed, using database links only",
                companyGuid);
        }

        return subscriptionIds.Distinct().ToList();
    }

    public Dictionary<string, PaymentSession> GetCheckoutPaymentSessions(IReadOnlyCollection<string> subscriptionIds)
    {
        if (subscriptionIds.Count == 0)
            return new Dictionary<string, PaymentSession>();

        return _paymentSessionRoRepo.GetData(x =>
                    x.OperatorTransactionId != null
                    && subscriptionIds.Contains(x.OperatorTransactionId)
                    && x.FromSubscriptionRenew == false,
                x => x.InvoiceData,
                x => x.InvoiceData.Company,
                x => x.InvoiceData.SubscriptionPlan)
            .ToList()
            .GroupBy(x => x.OperatorTransactionId!)
            .ToDictionary(x => x.Key, x => x.OrderBy(session => session.Created).First());
    }

    public Guid? GetOwnerCompanyGuid(Subscription subscription, PaymentSession? checkoutPaymentSession)
    {
        return StripeMetadataKeys.GetGuid(subscription.Metadata, StripeMetadataKeys.CompanyGuid)
               ?? checkoutPaymentSession?.InvoiceData?.CompanyGuid;
    }

    public PlatformSubscriptionPlan? GetBilledPlan(Subscription subscription, PaymentSession? checkoutPaymentSession,
        Guid? companyCurrentPlanGuid)
    {
        var recurringPlans = GetRecurringPlans();

        var metadataPlanGuid =
            StripeMetadataKeys.GetGuid(subscription.Metadata, StripeMetadataKeys.SubscriptionPlanGuid);
        var metadataPlan = metadataPlanGuid == null
            ? null
            : recurringPlans.FirstOrDefault(x => x.Guid == metadataPlanGuid);
        if (metadataPlan != null)
            return metadataPlan;

        var price = subscription.Items?.Data?.FirstOrDefault()?.Price;
        var companyCurrentPlan = companyCurrentPlanGuid == null
            ? null
            : recurringPlans.FirstOrDefault(x => x.Guid == companyCurrentPlanGuid);

        return StripeSubscriptionPlanMatcher.Match(
            price?.UnitAmount,
            price?.Currency,
            plan => StripeSubscriptionPlanMatcher.MatchesRecurring(plan, price?.Recurring?.Interval,
                price?.Recurring?.IntervalCount ?? 0),
            new[] { checkoutPaymentSession?.InvoiceData?.SubscriptionPlan, companyCurrentPlan },
            recurringPlans);
    }

    public async Task<Subscription?> GetSubscriptionAsync(string subscriptionId)
    {
        try
        {
            return await new SubscriptionService(StripeClient).GetAsync(subscriptionId,
                new SubscriptionGetOptions { Expand = new List<string> { "customer" } });
        }
        catch (StripeException e)
        {
            _logger.LogWarning(e, "Could not load Stripe subscription {SubscriptionId}", subscriptionId);
            return null;
        }
    }

    public async Task<List<Subscription>> GetSubscriptionsOfCustomerEmailsAsync(IEnumerable<string> emails)
    {
        var result = new List<Subscription>();
        var customerService = new CustomerService(StripeClient);
        var subscriptionService = new SubscriptionService(StripeClient);

        foreach (var email in emails.Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Checkout creates a new Stripe customer for every subscription, so one e-mail can have many.
            var customers = await customerService.ListAsync(new CustomerListOptions { Email = email, Limit = 100 });
            foreach (var customer in customers.Data)
            {
                var subscriptions = await subscriptionService.ListAsync(new SubscriptionListOptions
                {
                    Customer = customer.Id,
                    Status = "all",
                    Limit = 100,
                    Expand = new List<string> { "data.customer" }
                });
                result.AddRange(subscriptions.Data);
            }
        }

        return result.DistinctBy(x => x.Id).ToList();
    }

    private List<PlatformSubscriptionPlan> GetRecurringPlans()
    {
        // Inactive plans stay in - old subscriptions keep billing for plans that are no longer sold.
        return _recurringPlans ??= _platformSubscriptionPlanRoRepo
            .GetData(x => x.IsTrial == false && x.IsOneTimePayment == false)
            .ToList();
    }
}
