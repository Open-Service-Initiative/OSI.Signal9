using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.Web.Client.Pages;

/// <summary>What the tenant editor edits. Max devices is text because the CSP rules out FluentNumberInput.</summary>
public sealed class TenantForm
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public string Plan { get; set; } = nameof(SubscriptionPlan.Basic);
    public string MaxDevices { get; set; } = "25";

    /// <summary>The parent tenant's id, or empty for a top-level tenant. Only set when creating.</summary>
    public string ParentTenantId { get; set; } = string.Empty;

    public static TenantForm From(TenantDto tenant) => new()
    {
        Name = tenant.Name,
        Description = tenant.Description,
        ContactEmail = tenant.ContactEmail,
        ContactPhone = tenant.ContactPhone,
        Plan = tenant.Plan.ToString(),
        MaxDevices = tenant.MaxDevices.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // Values the API would refuse (a bad number) go as 0 and come back as a validation problem.
    public CreateTenantRequest ToCreateRequest() => new()
    {
        Name = Name,
        Description = Blank(Description),
        ContactEmail = ContactEmail,
        ContactPhone = Blank(ContactPhone),
        Plan = Enum.Parse<SubscriptionPlan>(Plan),
        MaxDevices = ParseMaxDevices(),
        ParentTenantId = Guid.TryParse(ParentTenantId, out var parent) ? parent : null,
    };

    public UpdateTenantRequest ToUpdateRequest() => new()
    {
        Name = Name,
        Description = Blank(Description),
        ContactEmail = ContactEmail,
        ContactPhone = Blank(ContactPhone),
        Plan = Enum.Parse<SubscriptionPlan>(Plan),
        MaxDevices = ParseMaxDevices(),
    };

    private int ParseMaxDevices() =>
        int.TryParse(MaxDevices, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
