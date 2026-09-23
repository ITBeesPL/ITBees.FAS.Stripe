using ITBees.FAS.Payments.Interfaces;
using ITBees.FAS.Payments.Interfaces.Models;
using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.Interfaces.Repository;
using ITBees.Models.Companies;
using ITBees.Models.Users;
using ITBees.RestfulApiControllers.Exceptions;
using ITBees.UserManager.Interfaces;
using Microsoft.Extensions.Logging;
using Stripe;

namespace ITBees.FAS.Stripe.Services;

public class CompanyStripeSubscriptionService : ICompanyStripeSubscriptionService
{
    private readonly IStripeSubscriptionLookupService _stripeSubscriptionLookupService;
    private readonly IAspCurrentUserService _aspCurrentUserService;
    private readonly IReadOnlyRepository<Company> _companyRoRepo;
    private readonly IReadOnlyRepository<UsersInCompany> _usersInCompanyRoRepo;
    private readonly IPaymentDbLoggerService _paymentDbLoggerService;
    private readonly ILogger<CompanyStripeSubscriptionService> _logger;

    public CompanyStripeSubscriptionService(
        IStripeSubscriptionLookupService stripeSubscriptionLookupService,
        IAspCurrentUserService aspCurrentUserService,
        IReadOnlyRepository<Company> companyRoRepo,
        IReadOnlyRepository<UsersInCompany> usersInCompanyRoRepo,
        IPaymentDbLoggerService paymentDbLoggerService,
        ILogger<CompanyStripeSubscriptionService> logger)
    {
        _stripeSubscriptionLookupService = stripeSubscriptionLookupService;
        _aspCurrentUserService = aspCurrentUserService;
        _companyRoRepo = companyRoRepo;
        _usersInCompanyRoRepo = usersInCompanyRoRepo;
        _paymentDbLoggerService = paymentDbLoggerService;
        _logger = logger;
    }

    public async Task<StripeSubscriptionVm> Update(CompanyStripeSubscriptionUm companyStripeSubscriptionUm)
    {
        var isPlatformOperator = _aspCurrentUserService.CurrentUserIsPlatformOperator();
        if (isPlatformOperator == false &&
            _aspCurrentUserService.TryCanIDoForCompany(TypeOfOperation.Rw, companyStripeSubscriptionUm.CompanyGuid) ==
            false)
        {
            throw new FasApiErrorException("You don't have access to this company", 403);
        }

        var subscription = await GetOwnedSubscription(companyStripeSubscriptionUm.SubscriptionId,
            companyStripeSubscriptionUm.CompanyGuid, isPlatformOperator);
        if (_stripeSubscriptionLookupService.IsBilling(subscription) == false)
            throw new FasApiErrorException("Subscription is no longer active", 400);

        var updated = await new SubscriptionService(_stripeSubscriptionLookupService.StripeClient).UpdateAsync(
            subscription.Id,
            new SubscriptionUpdateOptions
            {
                CancelAtPeriodEnd = companyStripeSubscriptionUm.CancelAtPeriodEnd,
                Expand = new List<string> { "customer" }
            });

        LogOperation(companyStripeSubscriptionUm.CancelAtPeriodEnd
                ? $"Subscription {subscription.Id} scheduled for cancellation at period end"
                : $"Scheduled cancellation of subscription {subscription.Id} withdrawn",
            subscription.Id);

        return ToVm(updated);
    }

    public async Task<StripeSubscriptionVm> Delete(Guid? companyGuid, string subscriptionId)
    {
        if (_aspCurrentUserService.CurrentUserIsPlatformOperator() == false)
            throw new FasApiErrorException("Only platform operator can cancel a subscription immediately", 403);

        var subscription = await GetOwnedSubscription(subscriptionId, companyGuid, true);
        if (_stripeSubscriptionLookupService.IsBilling(subscription) == false)
            throw new FasApiErrorException("Subscription is no longer active", 400);

        var cancelled = await CancelNow(subscription.Id,
            $"Cancelled by platform operator {GetCurrentUserDisplayName()}");
        LogOperation($"Subscription {subscription.Id} cancelled immediately", subscription.Id);

        return ToVm(cancelled);
    }

    public async Task<List<string>> CancelReplacedSubscriptions(Guid companyGuid, string newSubscriptionId)
    {
        var cancelledSubscriptionIds = new List<string>();
        var replacedSubscriptionIds = (await _stripeSubscriptionLookupService.GetCompanySubscriptionIdsAsync(companyGuid))
            .Where(x => x != newSubscriptionId);

        foreach (var subscriptionId in replacedSubscriptionIds)
        {
            try
            {
                var subscription = await _stripeSubscriptionLookupService.GetSubscriptionAsync(subscriptionId);
                if (subscription == null || _stripeSubscriptionLookupService.IsBilling(subscription) == false)
                    continue;

                var metadataCompanyGuid =
                    StripeMetadataKeys.GetGuid(subscription.Metadata, StripeMetadataKeys.CompanyGuid);
                if (metadataCompanyGuid != null && metadataCompanyGuid != companyGuid)
                    continue;

                await CancelNow(subscriptionId,
                    $"Replaced by subscription {newSubscriptionId} - company bought another plan");
                cancelledSubscriptionIds.Add(subscriptionId);

                _logger.LogInformation(
                    "Cancelled Stripe subscription {OldSubscriptionId} of company {CompanyGuid} replaced by {NewSubscriptionId}",
                    subscriptionId, companyGuid, newSubscriptionId);
                LogOperation(
                    $"Subscription {subscriptionId} cancelled - replaced by {newSubscriptionId} (company {companyGuid})",
                    subscriptionId);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Could not cancel Stripe subscription {OldSubscriptionId} of company {CompanyGuid} replaced by {NewSubscriptionId} - it keeps charging, cancel it manually",
                    subscriptionId, companyGuid, newSubscriptionId);
                LogOperation(
                    $"FAILED to cancel replaced subscription {subscriptionId} (company {companyGuid}): {e.Message}",
                    subscriptionId);
            }
        }

        return cancelledSubscriptionIds;
    }

    private async Task<Subscription> GetOwnedSubscription(string subscriptionId, Guid? companyGuid,
        bool isPlatformOperator)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new FasApiErrorException("Subscription id is required", 400);

        var subscription = await _stripeSubscriptionLookupService.GetSubscriptionAsync(subscriptionId)
                           ?? throw new FasApiErrorException("Subscription not found", 404);

        // Platform operator may manage any subscription, also ones not linked with any company.
        if (isPlatformOperator)
            return subscription;

        var checkoutPaymentSessions =
            _stripeSubscriptionLookupService.GetCheckoutPaymentSessions(new[] { subscription.Id });
        checkoutPaymentSessions.TryGetValue(subscription.Id, out var checkoutPaymentSession);
        var ownerCompanyGuid =
            _stripeSubscriptionLookupService.GetOwnerCompanyGuid(subscription, checkoutPaymentSession);

        var isOwned = ownerCompanyGuid != null
            ? ownerCompanyGuid == companyGuid
            : IsCustomerEmailOfCompanyUser(subscription.Customer?.Email, companyGuid);
        if (isOwned == false)
            throw new FasApiErrorException("Subscription not found", 404);

        return subscription;
    }

    private bool IsCustomerEmailOfCompanyUser(string? email, Guid? companyGuid)
    {
        if (string.IsNullOrWhiteSpace(email) || companyGuid == null)
            return false;

        return _usersInCompanyRoRepo
            .GetData(x => x.CompanyGuid == companyGuid.Value, x => x.UserAccount)
            .Any(x => string.Equals(x.UserAccount?.Email, email, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Subscription> CancelNow(string subscriptionId, string comment)
    {
        return await new SubscriptionService(_stripeSubscriptionLookupService.StripeClient).CancelAsync(
            subscriptionId,
            new SubscriptionCancelOptions
            {
                // No refund and no proration credit - the paid period is simply not renewed anymore.
                Prorate = false,
                InvoiceNow = false,
                CancellationDetails = new SubscriptionCancellationDetailsOptions { Comment = comment },
                Expand = new List<string> { "customer" }
            });
    }

    private StripeSubscriptionVm ToVm(Subscription subscription)
    {
        var checkoutPaymentSessions =
            _stripeSubscriptionLookupService.GetCheckoutPaymentSessions(new[] { subscription.Id });
        checkoutPaymentSessions.TryGetValue(subscription.Id, out var checkoutPaymentSession);
        var ownerCompanyGuid =
            _stripeSubscriptionLookupService.GetOwnerCompanyGuid(subscription, checkoutPaymentSession);
        var company = ownerCompanyGuid == null
            ? null
            : _companyRoRepo.GetData(x => x.Guid == ownerCompanyGuid.Value).FirstOrDefault();

        var billedPlan = _stripeSubscriptionLookupService.GetBilledPlan(subscription, checkoutPaymentSession,
            company?.CompanyPlatformSubscription?.SubscriptionPlanGuid);
        return new StripeSubscriptionVm(subscription, company, billedPlan,
            _stripeSubscriptionLookupService.IsBilling(subscription));
    }

    private string GetCurrentUserDisplayName()
    {
        var currentUser = _aspCurrentUserService.GetCurrentUser();
        return currentUser == null ? "unknown" : $"{currentUser.DisplayName} ({currentUser.Email})";
    }

    private void LogOperation(string message, string subscriptionId)
    {
        _paymentDbLoggerService.Log(new PaymentOperatorLog
        {
            Operator = "Stripe",
            Received = DateTime.Now,
            Event = message,
            JsonEvent = $"subscription_id={subscriptionId}; by={GetCurrentUserDisplayName()}"
        });
    }
}
