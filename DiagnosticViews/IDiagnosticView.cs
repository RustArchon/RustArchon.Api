// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.DiagnosticViews;

/// <summary>
/// A read-only diagnostic view (ADR-0007). Its <see cref="Definition"/> is static so the catalog can be built once at startup without creating a view
/// (which would need a database); <see cref="ExecuteAsync"/> reads through the Api's own services and returns only parts the definition declares.
/// </summary>
public interface IDiagnosticView
{
    static abstract ViewDefinition Definition { get; }

    Task<ViewAnswer> ExecuteAsync(ViewArguments arguments, CancellationToken cancellationToken);
}
