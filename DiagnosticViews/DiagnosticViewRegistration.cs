// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>Which type runs the view of a given name. Built at registration, so a view is found without creating any other.</summary>
public sealed record DiagnosticViewRegistration(ViewDefinition Definition, Type ViewType);

public static class DiagnosticViewRegistrationExtensions
{
    /// <summary>
    /// Registers a view (scoped, so it may use the database) and its definition. The catalog is a singleton built from every registered definition the first
    /// time it is asked for; the Api asks for it at startup so a bad definition stops the boot.
    /// </summary>
    public static IServiceCollection AddDiagnosticView<TView>(this IServiceCollection services)
        where TView : class, IDiagnosticView
    {
        services.AddScoped<TView>();
        services.AddSingleton(new DiagnosticViewRegistration(TView.Definition, typeof(TView)));
        services.TryAddSingleton(sp => new ViewCatalog(sp.GetServices<DiagnosticViewRegistration>().Select(r => r.Definition)));
        return services;
    }

    /// <summary>Registers every diagnostic view the Api has, and the runner that checks their answers. Add a new view here.</summary>
    public static IServiceCollection AddDiagnosticViews(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new ViewRunner(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddDiagnosticView<Views.ServerView>();
        services.AddDiagnosticView<Views.ServerPluginView>();
        services.AddDiagnosticView<Views.ServerUpdatesView>();
        services.AddDiagnosticView<Views.ServerRconRecentView>();
        services.AddDiagnosticView<Views.PlatformSettingsView>();
        services.AddDiagnosticView<Views.PluginKeysView>();
        return services;
    }

    public static IDiagnosticView? ResolveView(this IServiceProvider services, string name) =>
        services.GetServices<DiagnosticViewRegistration>().FirstOrDefault(r => r.Definition.Name == name) is { } registration
            ? (IDiagnosticView)services.GetRequiredService(registration.ViewType)
            : null;
}
