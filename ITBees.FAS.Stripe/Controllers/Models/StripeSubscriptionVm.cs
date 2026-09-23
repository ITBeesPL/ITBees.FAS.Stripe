using ITBees.Models.Companies;
using ITBees.Models.Payments;
using Stripe;

namespace ITBees.FAS.Stripe.Controllers.Models;

public class StripeSubscriptionVm
{
    public StripeSubscriptionVm()
    {
    }

    public StripeSubscriptionVm(Subscription subscription, Company? company, PlatformSubscriptionPlan? billedPlan,
        bool isBilling)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        var price = item?.Price;

        SubscriptionId = subscription.Id;
        Status = subscription.Status;
        IsBilling = isBilling;
        CustomerId = subscription.CustomerId;
        CustomerEmail = subscription.Customer?.Email;
        CompanyGuid = company?.Guid;
        CompanyName = company?.CompanyName;
        CompanyPlanName = company?.CompanyPlatformSubscription?.SubscriptionPlanName;
        CompanyPlanActiveTo = company?.CompanyPlatformSubscription?.SubscriptionActiveTo;
        SubscriptionPlanGuid = billedPlan?.Guid;
        SubscriptionPlanName = billedPlan?.PlanName;
        Amount = price?.UnitAmount / 100m;
        Currency = price?.Currency;
        Interval = price?.Recurring?.Interval;
        IntervalCount = price?.Recurring?.IntervalCount;
        Created = subscription.Created;
        CurrentPeriodStart = item?.CurrentPeriodStart;
        CurrentPeriodEnd = item?.CurrentPeriodEnd;
        CancelAtPeriodEnd = subscription.CancelAtPeriodEnd;
        CancelAt = subscription.CancelAt;
        CanceledAt = subscription.CanceledAt;
        EndedAt = subscription.EndedAt;
    }

    public string SubscriptionId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>True while Stripe still charges (or retries charging) the customer.</summary>
    public bool IsBilling { get; set; }

    public string? CustomerId { get; set; }
    public string? CustomerEmail { get; set; }
    public Guid? CompanyGuid { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Plan currently assigned to the company in the platform (may differ from the billed one).</summary>
    public string? CompanyPlanName { get; set; }

    public DateTime? CompanyPlanActiveTo { get; set; }

    /// <summary>Platform plan this subscription is billing for.</summary>
    public Guid? SubscriptionPlanGuid { get; set; }

    public string? SubscriptionPlanName { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Interval { get; set; }
    public long? IntervalCount { get; set; }
    public DateTime Created { get; set; }
    public DateTime? CurrentPeriodStart { get; set; }

    /// <summary>Next charge (or the end of access when cancellation at period end is scheduled).</summary>
    public DateTime? CurrentPeriodEnd { get; set; }

    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? CancelAt { get; set; }
    public DateTime? CanceledAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// How many other still-billing subscriptions the same company (or, when the company is unknown,
    /// the same customer e-mail) has. Anything above zero means the customer is charged twice.
    /// </summary>
    public int ParallelBillingSubscriptionsCount { get; set; }
}
