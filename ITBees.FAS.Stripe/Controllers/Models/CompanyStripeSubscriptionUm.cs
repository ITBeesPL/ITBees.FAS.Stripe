namespace ITBees.FAS.Stripe.Controllers.Models;

public class CompanyStripeSubscriptionUm
{
    public Guid CompanyGuid { get; set; }
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// true - stop renewing: no further charges, access stays until the end of the paid period;
    /// false - undo a scheduled cancellation.
    /// </summary>
    public bool CancelAtPeriodEnd { get; set; }
}
