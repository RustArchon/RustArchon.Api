// Copyright ©2026 Scott Blomfield

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace RustArchon.Api.Infrastructure.ObjectStorage;

/// <inheritdoc cref="IObjectStorage" />
/// <remarks>
/// A thin wrapper over <see cref="IAmazonS3"/> - every method here is a near-direct translation of one
/// S3 API call, with <see cref="AmazonS3Config.ForcePathStyle"/> set so this works against Garage (and
/// most other self-hosted S3-compatible stores) without a wildcard DNS setup for virtual-hosted-style
/// bucket addressing, which none of them need for the single bucket this Api actually uses.
/// </remarks>
/// <remarks>
/// The actual <see cref="AmazonS3Client"/> is built lazily, on first real use, rather than in the
/// constructor - deliberately, and not just style. This type is registered as a Singleton and injected
/// straight into <c>ThemeService</c> (Scoped), so simply resolving <c>ThemeService</c> from DI - which
/// <c>Program.cs</c>'s startup seeding does unconditionally - would otherwise construct this client too,
/// and the AWS SDK's own <see cref="AmazonS3Config"/> validation throws hard if
/// <see cref="ObjectStorageOptions.ServiceUrl"/> is empty (no <c>GARAGE_S3_ENDPOINT</c> configured).
/// That crashed the entire Api at startup in exactly the deployment this feature is supposed to
/// tolerate - one that hasn't run the Garage bootstrap yet - and happened before
/// <c>DefaultThemeSeeder</c>'s own try/catch (which already exists specifically to degrade gracefully
/// here) ever got a chance to run, since the throw came from resolving one of its arguments, not from
/// anything inside the try block. Deferring construction to first call means an unconfigured deployment
/// only fails the specific call that needed object storage - caught by <c>DefaultThemeSeeder</c> at
/// startup, or surfaced as a 500 on the one request that actually touched a theme asset - rather than
/// failing to start at all.
/// </remarks>
public class S3ObjectStorage : IObjectStorage
{
    private readonly ObjectStorageOptions _settings;
    private readonly Lazy<IAmazonS3> _client;
    private readonly string _bucketName;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = options.Value;
        _bucketName = _settings.BucketName;

        // Lazy<T>'s default thread-safety mode also caches a thrown exception and rethrows the same one
        // on every later access, rather than retrying the SDK validation each time - exactly right here,
        // since "GARAGE_S3_ENDPOINT is unset" doesn't change for the lifetime of this singleton.
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    private IAmazonS3 CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_settings.ServiceUrl))
        {
            throw new InvalidOperationException(
                "Object storage isn't configured - GARAGE_S3_ENDPOINT is unset. See DEPLOYMENT.md's " +
                "Garage bootstrap step.");
        }

        return new AmazonS3Client(
            _settings.AccessKey,
            _settings.SecretKey,
            new AmazonS3Config
            {
                ServiceURL = _settings.ServiceUrl,
                ForcePathStyle = true,
                // Garage (and every other self-hosted target this points at) has no AWS region of its
                // own - required by the SDK's config validation regardless, so this is a placeholder,
                // not a real region selection. Matches garage.toml's own s3_region = "garage".
                AuthenticationRegion = "garage",
                // AWSSDK.Core 4.x defaults both of these to WHEN_SUPPORTED, which adds a trailing CRC32
                // checksum to every request - real AWS S3 accepts that fine, but Garage (like most
                // other S3-compatible stores) rejects the resulting payload with "Invalid payload
                // signature" since it doesn't understand the newer checksum-trailer scheme. Confirmed
                // by hand: PutObjectAsync failed with exactly that error until these were set.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
    }

    /// <inheritdoc />
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new MemoryStream(content, writable: false);
        await _client.Value.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ObjectContent?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            using var response = await _client.Value.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            }, cancellationToken);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return new ObjectContent(buffer.ToArray(), response.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _client.Value.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = key
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        string? continuationToken = null;

        do
        {
            var listing = await _client.Value.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix,
                ContinuationToken = continuationToken
            }, cancellationToken);

            if (listing.S3Objects.Count > 0)
            {
                // DeleteObjectsAsync takes up to 1000 keys per call, which ListObjectsV2's own default
                // page size already respects - no separate chunking needed here.
                await _client.Value.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _bucketName,
                    Objects = listing.S3Objects
                        .Select(o => new KeyVersion { Key = o.Key })
                        .ToList()
                }, cancellationToken);
            }

            continuationToken = listing.IsTruncated == true ? listing.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }
}
