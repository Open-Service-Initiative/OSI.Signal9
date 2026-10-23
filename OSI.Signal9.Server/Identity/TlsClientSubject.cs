using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The certificate subject a <c>tls_client_auth</c> client registered (RFC 8705 §2.1.2): exactly one of the subject
/// distinguished name or a subject alternative name. It's kept in the OpenIddict application's properties under the
/// RFC's own metadata names.
/// </summary>
public sealed record TlsClientSubject(string Parameter, string Value)
{
    public const string SubjectDn = "tls_client_auth_subject_dn";
    public const string SanDns = "tls_client_auth_san_dns";
    public const string SanUri = "tls_client_auth_san_uri";
    public const string SanIp = "tls_client_auth_san_ip";
    public const string SanEmail = "tls_client_auth_san_email";

    private static readonly string[] Parameters = [SubjectDn, SanDns, SanUri, SanIp, SanEmail];

    /// <summary>The registered subject, or null unless exactly one parameter is registered.</summary>
    public static TlsClientSubject? From(ImmutableDictionary<string, JsonElement> properties)
    {
        var registered = Parameters
            .Where(properties.ContainsKey)
            .Select(name => (name, value: properties[name]))
            .ToList();
        if (registered is not [var (parameter, value)] || value.ValueKind is not JsonValueKind.String
            || value.GetString() is not { Length: > 0 } text)
            return null;
        return new TlsClientSubject(parameter, text);
    }

    public bool Matches(X509Certificate2 certificate) => Parameter switch
    {
        SubjectDn => TryParseDn(Value) is { } expected && DnEquals(expected, certificate.SubjectName),
        SanDns => Names(certificate, DnsTag).Any(name => string.Equals(name, Value, StringComparison.OrdinalIgnoreCase)),
        SanUri => Names(certificate, UriTag).Any(name => string.Equals(name, Value, StringComparison.Ordinal)),
        SanEmail => Names(certificate, EmailTag).Any(name => EmailEquals(name, Value)),
        SanIp => IPAddress.TryParse(Value, out var expected)
            && IpAddresses(certificate).Any(address => address.Equals(expected)),
        _ => false,
    };

    /// <summary>
    /// Compares distinguished names rather than their text (RFC 4517 distinguishedNameMatch): the same attributes in
    /// the same order, with values compared case-insensitively.
    /// </summary>
    private static bool DnEquals(X500DistinguishedName expected, X500DistinguishedName actual)
    {
        var left = expected.EnumerateRelativeDistinguishedNames().ToList();
        var right = actual.EnumerateRelativeDistinguishedNames().ToList();
        return left.Count == right.Count && left.Zip(right).All(pair =>
            !pair.First.HasMultipleElements && !pair.Second.HasMultipleElements
            && pair.First.GetSingleElementType().Value == pair.Second.GetSingleElementType().Value
            && string.Equals(pair.First.GetSingleElementValue(), pair.Second.GetSingleElementValue(),
                StringComparison.OrdinalIgnoreCase));
    }

    private static X500DistinguishedName? TryParseDn(string value)
    {
        try
        {
            return new X500DistinguishedName(value);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    // GeneralName tags (RFC 5280 §4.2.1.6).
    private static readonly Asn1Tag EmailTag = new(TagClass.ContextSpecific, 1);
    private static readonly Asn1Tag DnsTag = new(TagClass.ContextSpecific, 2);
    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);
    private static readonly Asn1Tag IpTag = new(TagClass.ContextSpecific, 7);

    private static IEnumerable<string> Names(X509Certificate2 certificate, Asn1Tag tag) =>
        GeneralNames(certificate, tag).Select(reader => reader.ReadCharacterString(UniversalTagNumber.IA5String, tag));

    private static IEnumerable<IPAddress> IpAddresses(X509Certificate2 certificate) =>
        GeneralNames(certificate, IpTag).Select(reader => new IPAddress(reader.ReadOctetString(IpTag)));

    /// <summary>Readers positioned at each subject alternative name with the given tag.</summary>
    private static List<AsnReader> GeneralNames(X509Certificate2 certificate, Asn1Tag tag)
    {
        var names = new List<AsnReader>();
        if (certificate.Extensions["2.5.29.17"] is not { } extension)
            return names;

        var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        while (sequence.HasData)
        {
            var encoded = sequence.ReadEncodedValue();
            var reader = new AsnReader(encoded, AsnEncodingRules.DER);
            if (reader.PeekTag() == tag)
                names.Add(reader);
        }

        return names;
    }

    /// <summary>The local part is compared exactly and the domain case-insensitively (RFC 5280 §7.5).</summary>
    private static bool EmailEquals(string left, string right)
    {
        var l = left.LastIndexOf('@');
        var r = right.LastIndexOf('@');
        return l > 0 && r > 0
            && left.AsSpan(0, l).SequenceEqual(right.AsSpan(0, r))
            && left.AsSpan(l + 1).Equals(right.AsSpan(r + 1), StringComparison.OrdinalIgnoreCase);
    }
}
