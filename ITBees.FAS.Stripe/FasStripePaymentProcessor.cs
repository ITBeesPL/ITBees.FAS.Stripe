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

        public FasActivePaymentSession CreatePaymentSession(FasPayment fasPayment)
        {
            var options = new SessionCreateOptions()
            {
                SuccessUrl = _platformSettingsService.GetSetting("PaymentSuccessUrl"),
                CancelUrl = _platformSettingsService.GetSetting("PaymentCancelUrl"),
                LineItems = new List<SessionLineItemOptions>(),
                Mode = fasPayment.Mode.ToString().ToLower(),
            };

            foreach (var product in fasPayment.Products)
            {
                options.LineItems.Add(new SessionLineItemOptions()
                {
                    PriceData = new SessionLineItemPriceDataOptions()
                    {
                        UnitAmount = product.Quantity,
                        Currency = product.Currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions()
                        {
                            Name = product.PaymentTitleOrProductName,
                        }
                    },
                    Quantity = product.Quantity

                });
            }

            var service = new SessionService();
            Session session = service.Create(options);

            return new FasActivePaymentSession(session.Url);
        }

        public StripeSettings StripeSettings { get; }
    }
}
