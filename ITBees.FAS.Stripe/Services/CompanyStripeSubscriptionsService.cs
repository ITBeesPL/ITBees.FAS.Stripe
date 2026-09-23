using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.Interfaces.Repository;
using ITBees.Models.Companies;
using ITBees.Models.Users;
using ITBees.RestfulApiControllers.Exceptions;
using ITBees.UserManager.Interfaces;
using Stripe;

namespace ITBees.FAS.Stripe.Services;

public class CompanyStripeSubscriptionsService : ICompanyStripeSubscriptionsService
{
    private readonly IStripeSubscriptionLookupService _stripeSubscriptionLookupService;
    private readonly IAspCurrentUserService _aspCurrentUserService;
    private readonly IReadOnlyRepository<Company> _companyRoRepo;
    private readonly IReadOnlyRepository<UsersInCompany> _usersInCompanyRoRepo;

    public CompanyStripeSubscriptionsService(
        IStripeSubscriptionLookupService stripeSubscriptionLookupService,
        IAspCurrentUserService aspCurrentUserService,
        IReadOnlyRepository<Company> companyRoRepo,
        IReadOnlyRepository<UsersInCompany> usersInCompanyRoRepo)
    {
        _stripeSubscriptionLookupService = stripeSubscriptionLookupService;
        _aspCurrentUserService = aspCurrentUserService;
        _companyRoRepo = companyRoRepo;
        _usersInCompanyRoRepo = usersInCompanyRoRepo;
    }

    public async Task<List<StripeSubscriptionVm>> GetAll(Guid companyGuid)
    {
        if (_aspCurrentUserService.CurrentUserIsPlatformOperator() == false &&
            _aspCurrentUserService.TryCanIDoForCompany(TypeOfOperation.Ro, companyGuid) == false)
        {
            throw new FasApiErrorException("You don't have access to this company", 403);
        }

        var company = _companyRoRepo.GetData(x => x.Guid == companyGuid).FirstOrDefault()
                      ?? throw new FasApiErrorException("Company not found", 404);

        var subscriptions = new List<Subscription>();
        foreach (var subscriptionId in await _stripeSubscriptionLookupService.GetCompanySubscriptionIdsAsync(companyGuid))
        {
            var subscription = await _stripeSubscriptionLookupService.GetSubscriptionAsync(subscriptionId);
            if (subscription != null)
                subscriptions.Add(subscription);
        }

        // Subscriptions not linked in the database (e.g. the checkout webhook never arrived) are found by
        // the e-mails of the company users; the ones linked to another company are dropped below.
        var companyEmails = _usersInCompanyRoRepo
            .GetData(x => x.CompanyGuid == companyGuid, x => x.UserAccount)
            .Select(x => x.UserAccount?.Email)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
        var subscriptionsByEmail =
            await _stripeSubscriptionLookupService.GetSubscriptionsOfCustomerEmailsAsync(companyEmails);
        subscriptions.AddRange(subscriptionsByEmail.Where(x => subscriptions.All(s => s.Id != x.Id)));

        var checkoutPaymentSessions = _stripeSubscriptionLookupService
            .GetCheckoutPaymentSessions(subscriptions.Select(x => x.Id).ToList());

        var result = new List<StripeSubscriptionVm>();
        foreach (var subscription in subscriptions)
        {
            checkoutPaymentSessions.TryGetValue(subscription.Id, out var checkoutPaymentSession);
            var ownerCompanyGuid =
                _stripeSubscriptionLookupService.GetOwnerCompanyGuid(subscription, checkoutPaymentSession);
            if (ownerCompanyGuid != null && ownerCompanyGuid != companyGuid)
                continue;

            var billedPlan = _stripeSubscriptionLookupService.GetBilledPlan(subscription, checkoutPaymentSession,
                company.CompanyPlatformSubscription?.SubscriptionPlanGuid);
            result.Add(new StripeSubscriptionVm(subscription, company, billedPlan,
                _stripeSubscriptionLookupService.IsBilling(subscription)));
        }

        var billingCount = result.Count(x => x.IsBilling);
        foreach (var vm in result.Where(x => x.IsBilling))
            vm.ParallelBillingSubscriptionsCount = billingCount - 1;

        return result
            .OrderByDescending(x => x.IsBilling)
            .ThenByDescending(x => x.Created)
            .ToList();
    }
}
