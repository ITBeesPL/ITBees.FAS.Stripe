using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.FAS.Stripe.Services;
using ITBees.Models.Roles;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ITBees.FAS.Stripe.Controllers;

[Authorize(Roles = Role.PlatformOperator)]
public class PlatformStripeSubscriptionsController : RestfulControllerBase<PlatformStripeSubscriptionsController>
{
    private readonly IPlatformStripeSubscriptionsService _platformStripeSubscriptionsService;

    public PlatformStripeSubscriptionsController(ILogger<PlatformStripeSubscriptionsController> logger,
        IPlatformStripeSubscriptionsService platformStripeSubscriptionsService) : base(logger)
    {
        _platformStripeSubscriptionsService = platformStripeSubscriptionsService;
    }

    [HttpGet]
    [Produces<List<StripeSubscriptionVm>>]
    public async Task<IActionResult> Get()
    {
        return await ReturnOkResultAsync(async () => await _platformStripeSubscriptionsService.GetAll());
    }
}
