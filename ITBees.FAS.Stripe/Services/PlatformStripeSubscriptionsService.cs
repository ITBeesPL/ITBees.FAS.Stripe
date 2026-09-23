using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.Interfaces.Repository;
using ITBees.Models.Companies;
using ITBees.RestfulApiControllers.Exceptions;
using ITBees.UserManager.Interfaces;
using Stripe;

namespace ITBees.FAS.Stripe.Services;

public class PlatformStripeSubscriptionsService : IPlatformStripeSubscriptionsService
{
    private readonly IStripeSubscriptionLookupService _stripeSubscriptionLookupService;
    private readonly IAspCurrentUserService _aspCurrentUserService;
    private readonly IReadOnlyRepository<Company> _companyRoRepo;

    public PlatformStripeSubscriptionsService(
        IStripeSubscriptionLookupService stripeSubscriptionLookupService,
        IAspCurrentUserService aspCurrentUserService,
        IReadOnlyRepository<Company> companyRoRepo)
    {
        _stripeSubscriptionLookupService = stripeSubscriptionLookupService;
        _aspCurrentUserService = aspCurrentUserService;
        _companyRoRepo = companyRoRepo;
    }

    public async Task<List<StripeSubscriptionVm>> GetAll()
    {
        if (_aspCurrentUserService.CurrentUserIsPlatformOperator() == false)
            throw new FasApiErrorException("Only platform operator can list all subscriptions", 403);

        // Without a status filter Stripe returns every subscription that has not been cancelled.
        var subscriptions = new List<Subscription>();
        var subscriptionService = new SubscriptionService(_stripeSubscriptionLookupService.StripeClient);
        await foreach (var subscription in subscriptionService.ListAutoPagingAsync(new SubscriptionListOptions
                       {
                           Limit = 100,
                           Expand = new List<string> { "data.customer" }
                       }))
        {
            subscriptions.Add(subscription);
        }

        var checkoutPaymentSessions = _stripeSubscriptionLookupService
            .GetCheckoutPaymentSessions(subscriptions.Select(x => x.Id).ToList());

        var ownerCompanyGuids = subscriptions
            .Select(x => _stripeSubscriptionLookupService.GetOwnerCompanyGuid(x,
                checkoutPaymentSessions.GetValueOrDefault(x.Id)))
            .Where(x => x != null)
            .Select(x => x!.Value)
            .Distinct()
            .ToList();
        var companies = _companyRoRepo.GetData(x => ownerCompanyGuids.Contains(x.Guid))
            .ToDictionary(x => x.Guid);

        var result = new List<StripeSubscriptionVm>();
        foreach (var subscription in subscriptions)
        {
            checkoutPaymentSessions.TryGetValue(subscription.Id, out var checkoutPaymentSession);
            var ownerCompanyGuid =
                _stripeSubscriptionLookupService.GetOwnerCompanyGuid(subscription, checkoutPaymentSession);
            var company = ownerCompanyGuid == null ? null : companies.GetValueOrDefault(ownerCompanyGuid.Value);

            var billedPlan = _stripeSubscriptionLookupService.GetBilledPlan(subscription, checkoutPaymentSession,
                company?.CompanyPlatformSubscription?.SubscriptionPlanGuid);
            result.Add(new StripeSubscriptionVm(subscription, company, billedPlan,
                _stripeSubscriptionLookupService.IsBilling(subscription)));
        }

        // Linked subscriptions are compared per company (one person may pay for several companies with one
        // e-mail); a legacy subscription not linked with any company is compared per customer e-mail.
        var billing = result.Where(x => x.IsBilling).ToList();
        var countByCompany = billing.Where(x => x.CompanyGuid != null)
            .GroupBy(x => x.CompanyGuid!.Value)
            .ToDictionary(x => x.Key, x => x.Count());
        var countByEmail = billing.Where(x => EmailKey(x) != null)
            .GroupBy(x => EmailKey(x)!)
            .ToDictionary(x => x.Key, x => x.Count());
        var unlinkedCountByEmail = billing.Where(x => x.CompanyGuid == null && EmailKey(x) != null)
            .GroupBy(x => EmailKey(x)!)
            .ToDictionary(x => x.Key, x => x.Count());
        foreach (var vm in billing)
        {
            var emailKey = EmailKey(vm);
            vm.ParallelBillingSubscriptionsCount = vm.CompanyGuid != null
                ? countByCompany[vm.CompanyGuid.Value] - 1 +
                  (emailKey == null ? 0 : unlinkedCountByEmail.GetValueOrDefault(emailKey))
                : emailKey == null ? 0 : countByEmail[emailKey] - 1;
        }

        return result
            .OrderByDescending(x => x.ParallelBillingSubscriptionsCount)
            .ThenBy(x => x.CompanyName ?? x.CustomerEmail)
            .ThenByDescending(x => x.Created)
            .ToList();
    }

    private static string? EmailKey(StripeSubscriptionVm vm)
    {
        return string.IsNullOrWhiteSpace(vm.CustomerEmail) ? null : vm.CustomerEmail.Trim().ToLowerInvariant();
    }
}
