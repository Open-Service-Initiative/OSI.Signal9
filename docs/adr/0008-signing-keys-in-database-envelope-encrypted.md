# Token signing and encryption keys live in the database, wrapped by a Key Vault key

OpenIddict must hold private keys in memory to sign tokens, so keeping them as Key Vault certificates protects nothing extra once loaded. Signal9 generates its RS256 signing keys and its encryption keys itself and stores them in SQL, each wrapped directly by one key-encryption key: a Key Vault key on Azure, or a certificate self-hosted. Each key is stored as a self-signed X.509 certificate whose start date is its activation date. Keys rotate every 90 days, the next key is published early, and every instance reloads without a restart. Retired keys are kept until the longest-lived refresh token they protected has expired.

## Considered Options

- **Key Vault certificates.** Rejected: rotation would happen outside the app's control, and self-hosted deployments would have no equivalent.
- **Wrapping keys through ASP.NET Data Protection.** Rejected: Data Protection isn't meant for long-term storage, and revoking one of its keys makes the wrapped keys unrecoverable. Data Protection is used only for cookies and the token cache.
