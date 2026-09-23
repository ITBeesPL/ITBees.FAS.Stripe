using ITBees.FAS.Payments;
using ITBees.FAS.Payments.Services;
using ITBees.Models.Payments;

namespace ITBees.FAS.Stripe;

/// <summary>
/// Finds the platform plan a Stripe subscription is really billing for, when the plan was not stored in
/// the subscription metadata (subscriptions created before that). Stripe prices are created inline at
/// checkout from the plan (unit amount = plan.Value, recurring interval from plan.Interval), so the amount
/// and the billing interval identify the plan; the hints (plan of the original checkout, the company's
/// current plan) only break ties. The company's current plan must not win blindly - with two parallel
/// subscriptions, or after an operator assigned another plan manually, it is not what Stripe charged for.
/// </summary>
public static class StripeSubscriptionPlanMatcher
{
    private const double PeriodToleranceDays = 3;

    /// <param name="amountMinor">Charged unit amount in minor units (grosze).</param>
    /// <param name="currency">Charged currency.</param>
    /// <param name="billingIntervalMatches">Tells whether a plan bills in the same interval as the subscription.</param>
    /// <param name="hints">Candidate plans in order of trust; nulls are ignored.</param>
    /// <param name="catalog">All paid recurring plans of the platform.</param>
    public static PlatformSubscriptionPlan? Match(long? amountMinor, string? currency,
        Func<PlatformSubscriptionPlan, bool> billingIntervalMatches,
        IEnumerable<PlatformSubscriptionPlan?> hints,
        IReadOnlyCollection<PlatformSubscriptionPlan> catalog)
    {
        var distinctHints = hints.Where(x => x != null).Select(x => x!).DistinctBy(x => x.Guid).ToList();

        bool AmountMatches(PlatformSubscriptionPlan plan) =>
            amountMinor.HasValue
            && decimal.Round(plan.Value * 100m) == amountMinor.Value
            && (string.IsNullOrEmpty(plan.Currency) || string.IsNullOrEmpty(currency)
                || string.Equals(plan.Currency, currency, StringComparison.OrdinalIgnoreCase));

        bool FullyMatches(PlatformSubscriptionPlan plan) => billingIntervalMatches(plan) && AmountMatches(plan);

        return distinctHints.FirstOrDefault(FullyMatches)
               ?? SingleOrNull(catalog.Where(FullyMatches))
               ?? distinctHints.FirstOrDefault(billingIntervalMatches)
               ?? SingleOrNull(catalog.Where(billingIntervalMatches))
               ?? distinctHints.FirstOrDefault();
    }

    /// <summary>Compares the plan with a Stripe price recurring interval (e.g. "month" x 3, "year" x 1).</summary>
    public static bool MatchesRecurring(PlatformSubscriptionPlan plan, string? interval, long intervalCount)
    {
        var expected = GetStripeInterval(plan);
        if (expected == null || string.IsNullOrEmpty(interval))
            return false;

        return ToComparable(expected.Value.Interval, expected.Value.Count) == ToComparable(interval, intervalCount);
    }

    /// <summary>Compares the plan with the billed period of an invoice line.</summary>
    public static bool MatchesPeriod(PlatformSubscriptionPlan plan, DateTime periodStart, DateTime periodEnd)
    {
        var expected = GetStripeInterval(plan);
        if (expected == null)
            return false;

        var (interval, count) = expected.Value;
        var expectedEnd = interval switch
        {
            "day" => periodStart.AddDays(count),
            "week" => periodStart.AddDays(7 * count),
            "year" => periodStart.AddYears((int)count),
            _ => periodStart.AddMonths((int)count)
        };

        return Math.Abs((expectedEnd - periodEnd).TotalDays) <= PeriodToleranceDays;
    }

    /// <summary>Same mapping FasStripePaymentProcessor uses when it creates the Stripe price.</summary>
    private static (string Interval, long Count)? GetStripeInterval(PlatformSubscriptionPlan plan)
    {
        FasBillingPeriod billingPeriod;
        try
        {
            billingPeriod = BillingPeriod.GetBillingPeriod(plan.Interval);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return billingPeriod switch
        {
            FasBillingPeriod.Daily => ("day", 1),
            FasBillingPeriod.Weekly => ("week", 1),
            FasBillingPeriod.Monthly => ("month", 1),
            FasBillingPeriod.Every3Months => ("month", 3),
            FasBillingPeriod.Every6Months => ("month", 6),
            FasBillingPeriod.Yearly => ("year", 1),
            _ => null
        };
    }

    private static string ToComparable(string interval, long count)
    {
        return interval switch
        {
            "year" => $"month:{12 * count}",
            "week" => $"day:{7 * count}",
            _ => $"{interval}:{count}"
        };
    }

    private static PlatformSubscriptionPlan? SingleOrNull(IEnumerable<PlatformSubscriptionPlan> plans)
    {
        var list = plans.DistinctBy(x => x.Guid).Take(2).ToList();
        return list.Count == 1 ? list[0] : null;
    }
}
