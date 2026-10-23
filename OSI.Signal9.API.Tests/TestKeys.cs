using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace OSI.Signal9.API.Tests;

/// <summary>Signing and encryption keys shared by every host in the test run, standing in for the stored keys.</summary>
internal static class TestKeys
{
    public static RsaSecurityKey Signing { get; } = new(RSA.Create(3072)) { KeyId = "test-signing" };

    public static RsaSecurityKey Encryption { get; } = new(RSA.Create(3072)) { KeyId = "test-encryption" };
}
