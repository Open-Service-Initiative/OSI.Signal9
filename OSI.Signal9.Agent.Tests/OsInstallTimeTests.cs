using OSI.Signal9.Agent.Services;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// The operating system's install time, which the agent reports at enrollment so the API can tell a wiped or re-imaged
/// machine from a reinstalled agent.
/// </summary>
[TestClass]
public sealed class OsInstallTimeTests
{
    [TestMethod]
    public void The_enrollment_request_carries_the_os_install_time()
    {
        var installedAt = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

        var request = new SystemInfoProvider(() => HardwareIdentity.None, () => installedAt).GetEnrollment("token", "certificate");

        Assert.AreEqual(installedAt, request.OsInstalledAt);
    }

    [TestMethod]
    public void An_unknown_install_time_is_left_out()
    {
        var request = new SystemInfoProvider(() => HardwareIdentity.None, () => null).GetEnrollment("token", "certificate");

        Assert.IsNull(request.OsInstalledAt);
    }

    [TestMethod]
    public void Windows_records_the_install_time_in_seconds_since_the_unix_epoch()
    {
        Assert.AreEqual(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero), OsInstallTime.FromInstallDate(1792920600));
    }

    [TestMethod]
    [DataRow(null, DisplayName = "Missing")]
    [DataRow(0, DisplayName = "Zero")]
    [DataRow("1792920600", DisplayName = "Not a DWORD")]
    public void A_missing_or_unusable_windows_install_date_is_unknown(object? value)
    {
        Assert.IsNull(OsInstallTime.FromInstallDate(value));
    }
}
