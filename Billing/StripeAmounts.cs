// Copyright ©2026 Scott Blomfield

using System;

namespace RustArchon.Api.Billing;

/// <summary>
/// Converts between this codebase's decimal-dollars convention and Stripe's own "smallest currency
/// unit" (cents, for USD) convention - shared by every place that talks to a Stripe API
/// (<see cref="StripeCheckoutService"/>, <see cref="StripeTaxService"/>), so the rounding rule is
/// defined exactly once.
/// </summary>
/// <remarks>
/// Assumes a two-decimal currency, true of every currency this codebase currently prices in (see
/// <c>Invoice.Currency</c>'s remarks) - a zero-decimal currency (e.g. JPY) would need its own branch
/// here before ever being offered.
/// </remarks>
internal static class StripeAmounts
{
    public static long ToMinorUnits(decimal amount) =>
        (long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinorUnits(long amount) => amount / 100m;
}
