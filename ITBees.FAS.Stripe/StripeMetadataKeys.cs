namespace ITBees.FAS.Stripe;

/// <summary>
/// Keys stored in the metadata of Stripe checkout sessions and subscriptions, so webhooks can map
/// a subscription back to the company and the plan it was bought for without guessing.
/// </summary>
public static class StripeMetadataKeys
{
    public const string PaymentSessionGuid = "paymentSessionGuid";
    public const string SubscriptionPlanGuid = "subscriptionPlanGuid";
    public const string CompanyGuid = "companyGuid";

    public static Guid? GetGuid(IDictionary<string, string>? metadata, string key)
    {
        if (metadata == null || !metadata.TryGetValue(key, out var value))
            return null;

        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}
