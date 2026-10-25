using System.Net;

namespace OSI.Signal9.Agent.Identity;

/// <summary>
/// Rotates the instance's key and certificate once the certificate is <see cref="AgentIdentityStore.RenewalMargin"/>
/// from its expiry (story 102): it creates a new key, offers its certificate to the API with the current one, and
/// switches to it only when the API has taken it. A rotation interrupted after the API took the certificate is finished
/// on the next attempt, by finding that the new certificate already authenticates.
/// </summary>
public sealed class AgentCertificateRotation(
    AgentIdentityStore identities,
    IAgentTransport transport,
    TimeProvider time,
    ILogger<AgentCertificateRotation> logger)
{
    /// <summary>
    /// The identity to run as: <paramref name="identity"/>, or a new one if its certificate was due for rotation.
    /// </summary>
    /// <exception cref="HttpRequestException">The rotation failed; the instance keeps its current identity.</exception>
    public async Task<AgentIdentity> RotateIfDueAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var next = identities.LoadNext();
        if (next is null)
        {
            if (time.GetUtcNow() < AgentIdentityStore.RenewalDue(identity.Certificate))
                return identity;
            next = identities.BeginRotation();
        }

        try
        {
            using var connection = new AgentConnection(identity, transport, time);
            await connection.Api.PutCertificateAsync(identity.AgentId, next.Certificate.ExportCertificatePem(), cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            identities.AbandonRotation();
            throw;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The current certificate no longer authenticates: an earlier attempt's rotation may have taken effect.
            if (!await AuthenticatesAsync(next, cancellationToken))
                throw;
            logger.LogInformation("Agent {AgentId} finished an interrupted certificate rotation", identity.AgentId);
        }

        identities.CompleteRotation();
        logger.LogInformation("Agent {AgentId} rotated its key and certificate", identity.AgentId);
        return identities.Load() ?? throw new InvalidOperationException("The rotated identity can't be loaded.");
    }

    private async Task<bool> AuthenticatesAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        using var connection = new AgentConnection(identity, transport, time);
        try
        {
            await connection.Tokens.GetAsync(cancellationToken);
            return true;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }
    }
}
