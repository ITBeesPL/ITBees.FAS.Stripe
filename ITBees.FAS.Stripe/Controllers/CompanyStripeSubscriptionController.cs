using ITBees.FAS.Stripe.Controllers.Models;
using ITBees.FAS.Stripe.Services;
using ITBees.RestfulApiControllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ITBees.FAS.Stripe.Controllers;

[Authorize]
public class CompanyStripeSubscriptionController : RestfulControllerBase<CompanyStripeSubscriptionController>
{
    private readonly ICompanyStripeSubscriptionService _companyStripeSubscriptionService;

    public CompanyStripeSubscriptionController(ILogger<CompanyStripeSubscriptionController> logger,
        ICompanyStripeSubscriptionService companyStripeSubscriptionService) : base(logger)
    {
        _companyStripeSubscriptionService = companyStripeSubscriptionService;
    }

    /// <summary>
    /// Schedules (cancelAtPeriodEnd = true) or withdraws (false) cancellation at the end of the paid period.
    /// </summary>
    [HttpPut]
    [Produces<StripeSubscriptionVm>]
    public async Task<IActionResult> Put([FromBody] CompanyStripeSubscriptionUm companyStripeSubscriptionUm)
    {
        return await ReturnOkResultAsync(async () =>
            await _companyStripeSubscriptionService.Update(companyStripeSubscriptionUm));
    }

    /// <summary>
    /// Cancels the subscription in Stripe immediately (platform operator only).
    /// </summary>
    [HttpDelete]
    [Produces<StripeSubscriptionVm>]
    public async Task<IActionResult> Delete(string subscriptionId, Guid? companyGuid)
    {
        return await ReturnOkResultAsync(async () =>
            await _companyStripeSubscriptionService.Delete(companyGuid, subscriptionId));
    }
}
