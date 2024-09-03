using ITBees.FAS.Payments;
using ITBees.FAS.Payments.Interfaces;
using ITBees.Interfaces.Platforms;
using Stripe;
using Stripe.Checkout;

namespace ITBees.FAS.Stripe
{
    public class FasStripePaymentProcessor : IFasPaymentProcessor
    {
        private readonly IPlatformSettingsService _platformSettingsService;

        public FasStripePaymentProcessor(IPlatformSettingsService platformSettingsService)
        {
            _platformSettingsService = platformSettingsService;
            StripeSettings = new StripeSettings(platformSettingsService);
            StripeConfiguration.ApiKey = StripeSettings.SecretKey;
        }

        public FasActivePaymentSession CreatePaymentSession(FasPayment fasPayment, bool oneTimePayment, string successUrl = "", string failUrl = "")
        {
            var successUrlSetting = string.IsNullOrEmpty(successUrl) ? _platformSettingsService.GetSetting("PaymentSuccessUrl") : successUrl;
            var failUrlSetting = string.IsNullOrEmpty(failUrl) ? _platformSettingsService.GetSetting("PaymentCancelUrl") : failUrl;
            var options = new SessionCreateOptions()
            {
                SuccessUrl = $"{successUrlSetting}?guid={fasPayment.PaymentSessionGuid}",
                CancelUrl = $"{failUrlSetting}?guid={fasPayment.PaymentSessionGuid}",
                LineItems = new List<SessionLineItemOptions>(),
                Mode = oneTimePayment ? "payment" : "subscription",
            };

            foreach (var product in fasPayment.Products)
            {
                SessionLineItemPriceDataRecurringOptions dataRecurringOptions = null;
                if (!oneTimePayment)
                {
                    dataRecurringOptions = new SessionLineItemPriceDataRecurringOptions()
                    {
                        Interval = GetStripeInterval(product.BillingPeriod),
                        IntervalCount = product.IntervalCount
                    };
                }

                options.LineItems.Add(new SessionLineItemOptions()
                {
                    PriceData = new SessionLineItemPriceDataOptions()
                    {
                        Currency = product.Currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions()
                        {
                            Name = product.PaymentTitleOrProductName,
                        },
                        UnitAmountDecimal = product.Price * 100 * product.Quantity,
                        Recurring = dataRecurringOptions
                    },
                    Quantity = product.Quantity
                });
            }

            var service = new SessionService();
            options.ClientReferenceId = fasPayment.PaymentSessionGuid.ToString();
            options.CustomerEmail = fasPayment.CustomerEmail;
            Session session = service.Create(options);

            return new FasActivePaymentSession(session.Url, session.Id);
        }


        private string GetStripeInterval(FasBillingPeriod productBillingPeriod)
        {
            switch (productBillingPeriod)
            {
                case FasBillingPeriod.Daily:
                    return "day";
                    break;
                case FasBillingPeriod.Weekly:
                    return "week";
                    break;
                case FasBillingPeriod.Monthly:
                    return "month";
                    break;
                case FasBillingPeriod.Every3Months:
                    return "month";
                    break;
                case FasBillingPeriod.Every6Months:
                    return "month";
                    break;
                case FasBillingPeriod.Yearly:
                    return "year";
                    break;
                case FasBillingPeriod.Custom:
                    return "custom";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(productBillingPeriod), productBillingPeriod, null);
            }
        }

        public bool ConfirmPayment(Guid paymentSessionGuid)
        {
            var sessionService = new SessionService();
            var options = new SessionListOptions
            {
                Limit = 100,
                Created = new DateRangeOptions
                {
                    GreaterThanOrEqual = DateTime.UtcNow.AddDays(-2)
                }
            };

            StripeList<Session> sessions = null;
            do
            {
                sessions = sessionService.List(options);

                if (sessions != null && sessions.Data != null && sessions.Data.Count > 0)
                {
                    var session = sessions.Data.FirstOrDefault(s => s.ClientReferenceId == paymentSessionGuid.ToString());
                    if (session != null)
                    {
                        return session.PaymentStatus == "paid";
                    }

                    options.StartingAfter = sessions.Data.Last().Id;
                }
            } while (sessions.HasMore);

            return false;
        }


        public string ProcessorName => "Stripe";

        public StripeSettings StripeSettings { get; }
    }
}
