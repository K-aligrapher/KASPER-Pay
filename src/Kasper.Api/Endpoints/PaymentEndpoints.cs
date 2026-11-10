using Kasper.Infrastructure.Commerce;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kasper.Api.Endpoints;

public static class PaymentEndpoints
{
    /// <summary>
    /// Read-only payment history plus spending limits. Purchases and invoices are created and approved
    /// through the normal chat → Final Review flow, so there is no endpoint that moves money directly.
    /// </summary>
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/payments", async (PaymentService svc, int? take, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(take ?? 50, ct)));

        app.MapGet("/api/payments/{id:guid}", async (Guid id, PaymentService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

        app.MapGet("/api/settings/guardrails", (PaymentService svc) => Results.Ok(svc.Guardrails()));

        app.MapPut("/api/settings/guardrails", async (GuardrailsDto dto, PaymentService svc, CancellationToken ct) =>
        {
            try { return Results.Ok(await svc.SaveGuardrailsAsync(dto, ct)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { code = "limits_invalid", message = ex.Message }); }
        });
    }
}
