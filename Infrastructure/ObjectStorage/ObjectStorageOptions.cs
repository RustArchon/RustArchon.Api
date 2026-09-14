// Copyright ©2026 Scott Blomfield

namespace RustArchon.Api.Infrastructure.ObjectStorage;

/// <summary>
/// Connection settings for <see cref="S3ObjectStorage"/>, bound from the <c>ObjectStorage</c>
/// configuration section - see <c>Program.cs</c> for exactly which flat env vars feed these.
/// </summary>
/// <remarks>
/// Named generically (not <c>GarageOptions</c>) on purpose - nothing in this class or
/// <see cref="S3ObjectStorage"/> is Garage-specific, only <c>docker-compose.yml</c>'s choice of which
/// image implements the S3 API these values point at. Swapping Garage for another S3-compatible
/// backend (or a hosted one) later is a config change, not a code change - see the theming design
/// discussion for why that portability mattered in choosing this shape.
/// </remarks>
public class ObjectStorageOptions
{
    /// <summary>
    /// The S3 API endpoint to talk to, e.g. <c>http://garage:3900</c> (container-to-container, the
    /// same pattern as <c>ApiBaseUrl</c> elsewhere in this stack) - never a public URL, since nothing
    /// outside <c>rustarchon-net</c> ever needs to reach this directly. See <c>docker-compose.yml</c>'s
    /// <c>garage</c> service.
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>The bucket every theme package is stored under - see <c>DEPLOYMENT.md</c>'s Garage
    /// bootstrap step, which creates it with this exact name.</summary>
    public string BucketName { get; set; } = "rustarchon-themes";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;
}
