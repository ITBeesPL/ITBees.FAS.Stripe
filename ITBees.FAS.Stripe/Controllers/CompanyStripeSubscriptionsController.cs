using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.FAS.Stripe.Services;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ITBees.FAS.Stripe.Controllers;

[Authorize]
public class CompanyStripeSubscriptionsController : RestfulControllerBase<CompanyStripeSubscriptionsController>
{
    private readonly ICompanyStripeSubscriptionsService _companyStripeSubscriptionsService;

    public CompanyStripeSubscriptionsController(ILogger<CompanyStripeSubscriptionsController> logger,
        ICompanyStripeSubscriptionsService companyStripeSubscriptionsService) : base(logger)
    {
        _companyStripeSubscriptionsService = companyStripeSubscriptionsService;
    }

    [HttpGet]
    [Produces<List<StripeSubscriptionVm>>]
    public async Task<IActionResult> Get(Guid companyGuid)
    {
        return await ReturnOkResultAsync(async () => await _companyStripeSubscriptionsService.GetAll(companyGuid));
    }
}
