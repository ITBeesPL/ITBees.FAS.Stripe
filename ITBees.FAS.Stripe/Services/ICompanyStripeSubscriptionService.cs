using ITBees.FAS.Stripe.Controllers.Models;

namespace ITBees.FAS.Stripe.Services;

public interface ICompanyStripeSubscriptionService
{
    /// <summary>
    /// Schedules (or undoes) cancellation at the end of the paid period - no further charges, access stays
    /// until the period end. Allowed for the company's users with write access and for platform operators.
    /// </summary>
    Task<StripeSubscriptionVm> Update(CompanyStripeSubscriptionUm companyStripeSubscriptionUm);

    /// <summary>Cancels the subscription immediately in Stripe (platform operators only, no refund).</summary>
    Task<StripeSubscriptionVm> Delete(Guid? companyGuid, string subscriptionId);

    /// <summary>
    /// After the company bought a new recurring plan, cancels every other still-billing Stripe subscription
    /// the company bought before - otherwise Stripe keeps charging the old plan in parallel with the new one.
    /// Never throws. Returns ids of the cancelled subscriptions.
    /// </summary>
    Task<List<string>> CancelReplacedSubscriptions(Guid companyGuid, string newSubscriptionId);
}
