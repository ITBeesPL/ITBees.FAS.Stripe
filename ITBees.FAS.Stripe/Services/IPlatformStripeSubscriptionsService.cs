using ITBees.FAS.Stripe.Controllers.Models;

namespace ITBees.FAS.Stripe.Services;

public interface IPlatformStripeSubscriptionsService
{
    /// <summary>
    /// Every not-cancelled Stripe subscription of the platform mapped to companies and plans
    /// (platform operators only). Parallel subscriptions of one company are counted on each row.
    /// </summary>
    Task<List<StripeSubscriptionVm>> GetAll();
}
