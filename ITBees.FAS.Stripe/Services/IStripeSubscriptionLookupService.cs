using ITBees.FAS.Payments.Interfaces.Models;
using ITBees.Models.Payments;
using Stripe;

namespace ITBees.FAS.Stripe.Services;

/// <summary>
/// Shared lookups between Stripe subscriptions and FAS data (companies, checkout payment sessions, plans).
/// </summary>
public interface IStripeSubscriptionLookupService
{
    StripeClient StripeClient { get; }

    /// <summary>Stripe statuses in which a subscription still charges (or will retry charging) the customer.</summary>
    bool IsBilling(Subscription subscription);

    /// <summary>
    /// Ids of every Stripe subscription bought by the company through a platform plan checkout: linked by the
    /// checkout webhook in the database (renewal sessions are ignored on purpose - they can be attributed by the
    /// legacy e-mail fallback) plus the ones carrying the company in Stripe metadata, so a checkout closed only
    /// by the browser redirect (webhook not delivered yet) is found too.
    /// </summary>
    Task<List<string>> GetCompanySubscriptionIdsAsync(Guid companyGuid);

    /// <summary>Original checkout payment session of each subscription id (with invoice data, company and plan).</summary>
    Dictionary<string, PaymentSession> GetCheckoutPaymentSessions(IReadOnlyCollection<string> subscriptionIds);

    /// <summary>Company the subscription belongs to: metadata first, then its checkout payment session.</summary>
    Guid? GetOwnerCompanyGuid(Subscription subscription, PaymentSession? checkoutPaymentSession);

    /// <summary>Platform plan the subscription is really billing for.</summary>
    PlatformSubscriptionPlan? GetBilledPlan(Subscription subscription, PaymentSession? checkoutPaymentSession,
        Guid? companyCurrentPlanGuid);

    Task<Subscription?> GetSubscriptionAsync(string subscriptionId);

    Task<List<Subscription>> GetSubscriptionsOfCustomerEmailsAsync(IEnumerable<string> emails);
}
