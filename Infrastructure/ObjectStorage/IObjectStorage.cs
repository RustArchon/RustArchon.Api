// Copyright ©2026 Scott Blomfield

using System.Threading;
using System.Threading.Tasks;

namespace RustArchon.Api.Infrastructure.ObjectStorage;

/// <summary>One object read back from storage - the raw bytes plus enough metadata to serve it
/// correctly over HTTP.</summary>
/// <param name="Content">
/// The object's full bytes, already buffered in memory - theme assets are small (a stylesheet, an
/// icon, a font file), never large enough to warrant streaming this through as an open connection to
/// the storage backend. See <see cref="IObjectStorage.GetAsync"/>'s remarks.
/// </param>
/// <param name="ContentType">Recorded at upload time from the file's extension - see
/// <c>ThemePackageValidator</c>'s content-type map, the only place this is decided.</param>
public record ObjectContent(byte[] Content, string ContentType);

/// <summary>
/// The one place RustArchon.Api talks to the S3-compatible object store (Garage in this deployment -
/// see <see cref="ObjectStorageOptions"/>'s remarks on why this interface names nothing Garage-specific).
/// Backs the theming feature's uploaded package assets: a theme package is a small file tree (CSS,
/// images, fonts), not a single value, so it doesn't fit Postgres/Valkey the way the rest of this
/// Api's data does.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="key"/>, overwriting whatever
    /// (if anything) was there before.</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one object back, or <c>null</c> if <paramref name="key"/> doesn't exist - never throws for
    /// a missing key, since "this theme asset isn't there" is an ordinary, expected outcome (a bad path
    /// in a request, an asset a theme's manifest doesn't actually reference) rather than an error.
    /// </summary>
    Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every object whose key starts with <paramref name="prefix"/> - how a theme's entire
    /// package is removed in one call (<c>themes/{themeId}/</c>) without the caller having to know or
    /// re-list every individual file it once uploaded.
    /// </summary>
    Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes exactly one object - what <c>ThemeService.ReplaceAsync</c> uses to clean up a single file
    /// that the theme's new content no longer includes (an image or font the admin removed while
    /// editing), without touching every other object still under that theme's prefix the way
    /// <see cref="DeleteByPrefixAsync"/> would.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
