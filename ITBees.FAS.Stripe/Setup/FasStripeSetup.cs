using ITBees.FAS.Setup;
using ITBees.FAS.Stripe.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ITBees.FAS.Stripe.Setup;

/// <summary>
/// Picked up automatically by FasSetup.RegisterAllFasDependencies (the host already references this
/// assembly through PaymentsManagerSetup.RegisterDefaultPaymentProvider&lt;FasStripePaymentProcessor&gt;).
/// </summary>
public class FasStripeSetup : IFasDependencyRegistration
{
    public void Register(IServiceCollection services, IConfigurationRoot configurationRoot)
    {
        services.TryAddScoped<IStripeSubscriptionLookupService, StripeSubscriptionLookupService>();
        services.TryAddScoped<ICompanyStripeSubscriptionsService, CompanyStripeSubscriptionsService>();
        services.TryAddScoped<ICompanyStripeSubscriptionService, CompanyStripeSubscriptionService>();
        services.TryAddScoped<IPlatformStripeSubscriptionsService, PlatformStripeSubscriptionsService>();
    }
}
