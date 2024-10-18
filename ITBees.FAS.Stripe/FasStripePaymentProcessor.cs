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
                    var stripeInterval = GetStripeInterval(product.BillingPeriod);
                    dataRecurringOptions = new SessionLineItemPriceDataRecurringOptions()
                    {
                        Interval = stripeInterval.Name,
                        IntervalCount = stripeInterval.Count
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


        private Interval GetStripeInterval(FasBillingPeriod productBillingPeriod)
        {
            switch (productBillingPeriod)
            {
                case FasBillingPeriod.Daily:
                    return new Interval("day", 1);
                    break;
                case FasBillingPeriod.Weekly:
                    return new Interval("week", 1);
                    break;
                case FasBillingPeriod.Monthly:
                    return new Interval("month", 1);
                    break;
                case FasBillingPeriod.Every3Months:
                    return new Interval("month", 3);
                    break;
                case FasBillingPeriod.Every6Months:
                    return new Interval("month", 6);
                    break;
                case FasBillingPeriod.Yearly:
                    return new Interval("year", 1);
                    break;
                case FasBillingPeriod.Custom:
                    return new Interval("custom", 1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(productBillingPeriod), productBillingPeriod, null);
            }
        }

        private class Interval
        {
            public Interval(string name, int count)
            {
                Name = name;
                Count = count;
            }

            public string Name { get; set; }
            public int Count { get; set; }
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
