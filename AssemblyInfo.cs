// Copyright ©2026 Scott Blomfield

using System.Runtime.CompilerServices;

// Lets RustArchon.Api.Tests drive an internal method directly - e.g. SubscriptionScheduleService.RunPassAsync
// and DunningService.RunPassAsync, both internal specifically so a test can call one pass without waiting
// out the real hourly timer (see either class's own remarks).
[assembly: InternalsVisibleTo("RustArchon.Api.Tests")]
