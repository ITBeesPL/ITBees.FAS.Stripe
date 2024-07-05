using ITBees.Interfaces.Platforms;

namespace ITBees.FAS.Stripe;

public class StripeSettings
{
    public StripeSettings(IPlatformSettingsService platformSettingsService)
    {
        SecretKey = platformSettingsService.GetSetting("StripeSecretKey");
        PublishableKey = platformSettingsService.GetSetting("StripePublishableKey");
    }

    public string SecretKey { get; set; }
    public string PublishableKey { get; set; }
}