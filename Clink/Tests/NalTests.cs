using Clink.Encode;

namespace Clink.Tests;

/// <summary>Length-prefixed NAL parsing (<see cref="Nal"/>): a broken length stops the walk instead of throwing.</summary>
internal static class NalTests
{
    [UnitTest]
    public static void ToAnnexB_stops_at_a_length_that_runs_past_the_end_even_near_int_MaxValue()
    {
        byte[] good = { 0, 0, 0, 2, 0x65, 0x88 };
        Check.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }, Nal.ToAnnexB(good, null, hevc: false));
        foreach (byte[] bad in new[]
        {
            new byte[] { 0, 0, 0, 2, 0x65, 0x88, 0x7F, 0xFF, 0xFF, 0xFF, 1, 2 },   // 0x7FFFFFFF: i + len overflowed and passed the check
            new byte[] { 0, 0, 0, 2, 0x65, 0x88, 0x7F, 0xFF, 0xFF, 0xFC, 1, 2 },
            new byte[] { 0, 0, 0, 2, 0x65, 0x88, 0x80, 0, 0, 0, 1, 2 },              // negative
            new byte[] { 0, 0, 0, 2, 0x65, 0x88, 0, 0, 0, 3, 1, 2 },                 // one byte short
        })
            Check.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0x88 }, Nal.ToAnnexB(bad, null, hevc: false), "the good NAL before it, nothing after");
    }
}
