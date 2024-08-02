﻿using ITBees.FAS.Payments;
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
                SuccessUrl = $"{_platformSettingsService.GetSetting("PaymentSuccessUrl")}?guid={fasPayment.PaymentSessionGuid}",
                CancelUrl = $"{_platformSettingsService.GetSetting("PaymentCancelUrl")}?guid={fasPayment.PaymentSessionGuid}",
                LineItems = new List<SessionLineItemOptions>(),
                Mode = fasPayment.Mode.ToString().ToLower(),
            };

            foreach (var product in fasPayment.Products)
            {
                options.LineItems.Add(new SessionLineItemOptions()
                {
                    PriceData = new SessionLineItemPriceDataOptions()
                    {

                        Currency = product.Currency,
                        ProductData = new SessionLineItemPriceDataProductDataOptions()
                        {
                            Name = product.PaymentTitleOrProductName,
                        },
                        UnitAmountDecimal = product.Price * product.Quantity,
                        Recurring = new SessionLineItemPriceDataRecurringOptions()
                        {
                            Interval = GetStripeInterval(product.BillingPeriod),
                            IntervalCount = product.IntervalCount
                        }
                    },
                    Quantity = product.Quantity

                });
            }

            var service = new SessionService();
            options.ClientReferenceId = fasPayment.ToString();
            options.CustomerEmail = fasPayment.CustomerEmail;
            Session session = service.Create(options);

            return new FasActivePaymentSession(session.Url,session.Id);
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
                Limit = 1
            };

            // Add the client_reference_id filter
            options.AddExtraParam("client_reference_id", paymentSessionGuid);

            var sessions = sessionService.List(options);

            if (sessions != null && sessions.Data != null && sessions.Data.Count > 0)
            {
                var session = sessions.Data[0];
                return session.PaymentStatus == "paid";
            }

            return false;
        }

        public string ProcessorName => "Stripe";

        public StripeSettings StripeSettings { get; }
    }
}
