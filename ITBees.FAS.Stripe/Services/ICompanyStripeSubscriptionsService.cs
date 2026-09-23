using ITBees.FAS.Stripe.Controllers.Models;

namespace ITBees.FAS.Stripe.Services;

public interface ICompanyStripeSubscriptionsService
{
    /// <summary>All Stripe subscriptions of the company, still-billing ones first.</summary>
    Task<List<StripeSubscriptionVm>> GetAll(Guid companyGuid);
}
