using System.IO;
using Clink.Capture;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The yellow capture border: the decision behind MonitorCapture.BorderState (no Windows Graphics Capture involved) and the one-time-notice flag.</summary>
internal static class CaptureBorderTests
{
    [UnitTest]
    public static void A_missing_IsBorderRequired_means_an_unsupported_OS()
    {
        var (state, _) = MonitorCapture.DecideBorder(propertyPresent: false, accessTypePresent: false, accessStatus: null, setException: null);
        Check.Equal(CaptureBorderState.UnsupportedOs, state);
    }

    [UnitTest]
    public static void Allowed_access_and_a_successful_set_hides_the_border()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "Allowed", null);
        Check.Equal(CaptureBorderState.Hidden, state);
        Check.Equal("Allowed", detail);
    }

    [UnitTest]
    public static void A_denied_request_is_Denied_with_the_status_even_when_the_set_succeeds()
    {
        foreach (var status in new[] { "DeniedBySystem", "DeniedByUser", "UserPromptRequired", "NotDeclaredByApp" })
        {
            var (state, detail) = MonitorCapture.DecideBorder(true, true, status, null);
            Check.Equal(CaptureBorderState.Denied, state);
            Check.Equal(status, detail);
        }
    }

    [UnitTest]
    public static void A_denied_request_is_Denied_even_when_the_set_throws()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "DeniedBySystem", new InvalidOperationException("nope"));
        Check.Equal(CaptureBorderState.Denied, state);
        Check.Equal("DeniedBySystem", detail);
    }

    [UnitTest]
    public static void A_timed_out_request_with_a_working_set_is_Unconfirmed()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "Timeout", null);
        Check.Equal(CaptureBorderState.Unconfirmed, state);
        Check.Equal("Timeout", detail);
    }

    [UnitTest]
    public static void A_throwing_request_with_a_working_set_is_Unconfirmed_with_the_message()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "Error: boom", null);
        Check.Equal(CaptureBorderState.Unconfirmed, state);
        Check.Equal("boom", detail);
    }

    [UnitTest]
    public static void Without_the_access_class_a_working_set_is_Unconfirmed()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, false, null, null);
        Check.Equal(CaptureBorderState.Unconfirmed, state);
        Check.Equal("GraphicsCaptureAccess unavailable", detail);
    }

    [UnitTest]
    public static void A_failed_set_is_SetFailed_with_the_message_whatever_the_unknown_access()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "Timeout", new InvalidOperationException("boom"));
        Check.Equal(CaptureBorderState.SetFailed, state);
        Check.Equal("boom", detail);
    }

    [UnitTest]
    public static void Allowed_access_and_a_failed_set_is_SetFailed_with_the_message()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, "Allowed", new InvalidOperationException("boom"));
        Check.Equal(CaptureBorderState.SetFailed, state);
        Check.Equal("boom", detail);
    }

    [UnitTest]
    public static void An_absent_property_wins_over_an_Allowed_access()
    {
        var (state, _) = MonitorCapture.DecideBorder(propertyPresent: false, accessTypePresent: true, accessStatus: "Allowed", setException: null);
        Check.Equal(CaptureBorderState.UnsupportedOs, state);
    }

    [UnitTest]
    public static void A_present_access_type_with_no_status_and_a_working_set_is_Unconfirmed()
    {
        var (state, detail) = MonitorCapture.DecideBorder(true, true, null, null);
        Check.Equal(CaptureBorderState.Unconfirmed, state);
        Check.Equal("Unknown", detail);
    }

    [UnitTest]
    public static void The_border_toast_shows_once_per_run_until_dismissed_and_never_for_Hidden_or_Unconfirmed()
    {
        foreach (var s in new[] { CaptureBorderState.Hidden, CaptureBorderState.Unconfirmed, CaptureBorderState.Denied, CaptureBorderState.UnsupportedOs, CaptureBorderState.SetFailed })
        {
            bool quiet = s is CaptureBorderState.Hidden or CaptureBorderState.Unconfirmed;
            foreach (var dismissed in new[] { false, true })
                foreach (var shown in new[] { false, true })
                    Check.Equal(!quiet && !dismissed && !shown, AppController.ShouldNotify(s, dismissed, shown));
        }
    }

    [UnitTest]
    public static void The_border_notice_flag_defaults_off_and_survives_a_save()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var s = Settings.Load();
        Check.False(s.CaptureBorderNoticeDismissed);
        s.CaptureBorderNoticeDismissed = true;
        s.Save();
        Check.True(Settings.Load().CaptureBorderNoticeDismissed);
    }
}
