using System.IO;

namespace Clink.Encode;

internal sealed class ParameterSets
{
    public byte[]? Vps, Sps, Pps;
    public bool Complete(bool hevc) => Sps != null && Pps != null && (!hevc || Vps != null);
    public ParameterSets Clone() => new() { Vps = Vps, Sps = Sps, Pps = Pps };
}

/// <summary>Annex B / MP4 NAL helpers for H.264 and HEVC.</summary>
internal static class Nal
{
    public const int H264_SPS = 7, H264_PPS = 8, H264_AUD = 9, H264_IDR = 5, H264_SEI = 6;
    public const int HEVC_VPS = 32, HEVC_SPS = 33, HEVC_PPS = 34, HEVC_AUD = 35;

    public static int Type(ReadOnlySpan<byte> nal, bool hevc) => hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;

    public static bool IsParameterSet(int type, bool hevc) =>
        hevc ? type is HEVC_VPS or HEVC_SPS or HEVC_PPS : type is H264_SPS or H264_PPS;

    public static bool IsKeyframeType(int type, bool hevc) => hevc ? type >= 16 && type <= 21 : type == H264_IDR;

    /// <summary>Splits an Annex B byte stream into NAL unit (offset,length) pairs excluding start codes.</summary>
    public static List<(int Offset, int Length)> Split(ReadOnlySpan<byte> data)
    {
        var list = new List<(int, int)>();
        int n = data.Length;
        int i = 0;
        int start = -1;
        while (i + 2 < n)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = i;
                    while (end > start && data[end - 1] == 0) end--; // trailing zero / 4-byte start code
                    if (end > start) list.Add((start, end - start));
                }
                i += 3;
                start = i;
            }
            else i++;
        }
        if (start >= 0 && start < n)
        {
            int end = n;
            while (end > start && data[end - 1] == 0) end--;
            if (end > start) list.Add((start, end - start));
        }
        return list;
    }

    public static bool ContainsKeyframe(ReadOnlySpan<byte> annexB, bool hevc)
    {
        foreach (var (off, len) in Split(annexB))
            if (IsKeyframeType(Type(annexB.Slice(off, len), hevc), hevc)) return true;
        return false;
    }

    /// <summary>
    /// Converts Annex B to 4-byte length-prefixed NALs (as stored in MP4 avc1/hvc1 tracks), dropping AUDs and
    /// parameter sets (which go to the sample description instead). Parameter sets found are stored in <paramref name="ps"/>.
    /// </summary>
    public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB, bool hevc, ParameterSets ps)
    {
        var nals = Split(annexB);
        int total = 0;
        foreach (var (off, len) in nals)
        {
            int t = Type(annexB.Slice(off, len), hevc);
            if (IsParameterSet(t, hevc))
            {
                var copy = annexB.Slice(off, len).ToArray();
                if (hevc) { if (t == HEVC_VPS) ps.Vps = copy; else if (t == HEVC_SPS) ps.Sps = copy; else ps.Pps = copy; }
                else { if (t == H264_SPS) ps.Sps = copy; else ps.Pps = copy; }
                continue;
            }
            if ((hevc && t == HEVC_AUD) || (!hevc && t == H264_AUD)) continue;
            total += 4 + len;
        }
        var dst = new byte[total];
        int w = 0;
        foreach (var (off, len) in nals)
        {
            int t = Type(annexB.Slice(off, len), hevc);
            if (IsParameterSet(t, hevc) || (hevc && t == HEVC_AUD) || (!hevc && t == H264_AUD)) continue;
            dst[w++] = (byte)(len >> 24); dst[w++] = (byte)(len >> 16); dst[w++] = (byte)(len >> 8); dst[w++] = (byte)len;
            annexB.Slice(off, len).CopyTo(dst.AsSpan(w));
            w += len;
        }
        return dst;
    }

    /// <summary>Converts length-prefixed (4 byte) NALs back to Annex B, prepending parameter sets when <paramref name="withParams"/>.</summary>
    public static byte[] ToAnnexB(ReadOnlySpan<byte> lengthPrefixed, ParameterSets? withParams, bool hevc)
    {
        using var ms = new MemoryStream();
        ReadOnlySpan<byte> sc = stackalloc byte[] { 0, 0, 0, 1 };
        if (withParams != null)
        {
            if (hevc && withParams.Vps != null) { ms.Write(sc); ms.Write(withParams.Vps); }
            if (withParams.Sps != null) { ms.Write(sc); ms.Write(withParams.Sps); }
            if (withParams.Pps != null) { ms.Write(sc); ms.Write(withParams.Pps); }
        }
        int i = 0;
        while (i + 4 <= lengthPrefixed.Length)
        {
            int len = (lengthPrefixed[i] << 24) | (lengthPrefixed[i + 1] << 16) | (lengthPrefixed[i + 2] << 8) | lengthPrefixed[i + 3];
            i += 4;
            if (len < 0 || i + len > lengthPrefixed.Length) break;
            ms.Write(sc);
            ms.Write(lengthPrefixed.Slice(i, len));
            i += len;
        }
        return ms.ToArray();
    }

    public static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nal)
    {
        var o = new List<byte>(nal.Length);
        int zeros = 0;
        for (int i = 0; i < nal.Length; i++)
        {
            byte b = nal[i];
            if (zeros >= 2 && b == 3 && i + 1 < nal.Length && nal[i + 1] <= 3) { zeros = 0; continue; }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }

    public static byte[] BuildAvcC(byte[] sps, byte[] pps)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(1);          // configurationVersion
        ms.WriteByte(sps[1]);     // AVCProfileIndication
        ms.WriteByte(sps[2]);     // profile_compatibility
        ms.WriteByte(sps[3]);     // AVCLevelIndication
        ms.WriteByte(0xFF);       // lengthSizeMinusOne = 3
        ms.WriteByte(0xE1);       // numOfSequenceParameterSets = 1
        ms.WriteByte((byte)(sps.Length >> 8)); ms.WriteByte((byte)sps.Length); ms.Write(sps);
        ms.WriteByte(1);          // numOfPictureParameterSets
        ms.WriteByte((byte)(pps.Length >> 8)); ms.WriteByte((byte)pps.Length); ms.Write(pps);
        return ms.ToArray();
    }

    /// <summary>Builds an HEVCDecoderConfigurationRecord from VPS/SPS/PPS (general profile/tier/level parsed from the SPS).</summary>
    public static byte[] BuildHvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        var rbsp = RemoveEmulationPrevention(sps.AsSpan(2)); // skip 2-byte NAL header
        var br = new BitReader(rbsp);
        br.Read(4);                         // sps_video_parameter_set_id
        int maxSubLayersMinus1 = (int)br.Read(3);
        int temporalIdNesting = (int)br.Read(1);
        // profile_tier_level (general part)
        int profileSpace = (int)br.Read(2);
        int tierFlag = (int)br.Read(1);
        int profileIdc = (int)br.Read(5);
        uint compat = br.Read(32);
        ulong constraint = ((ulong)br.Read(32) << 16) | br.Read(16); // 48 bits
        int levelIdc = (int)br.Read(8);
        // sub_layer flags
        var subProfilePresent = new bool[maxSubLayersMinus1];
        var subLevelPresent = new bool[maxSubLayersMinus1];
        for (int i = 0; i < maxSubLayersMinus1; i++) { subProfilePresent[i] = br.Read(1) == 1; subLevelPresent[i] = br.Read(1) == 1; }
        if (maxSubLayersMinus1 > 0) for (int i = maxSubLayersMinus1; i < 8; i++) br.Read(2);
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (subProfilePresent[i]) { br.Read(32); br.Read(32); br.Read(24); } // 88 bits
            if (subLevelPresent[i]) br.Read(8);
        }
        br.ReadUE();                        // sps_seq_parameter_set_id
        int chromaFormatIdc = (int)br.ReadUE();
        if (chromaFormatIdc == 3) br.Read(1);
        br.ReadUE(); br.ReadUE();           // pic width / height
        if (br.Read(1) == 1) { br.ReadUE(); br.ReadUE(); br.ReadUE(); br.ReadUE(); }
        int bitDepthLumaMinus8 = (int)br.ReadUE();
        int bitDepthChromaMinus8 = (int)br.ReadUE();

        using var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.WriteByte((byte)((profileSpace << 6) | (tierFlag << 5) | profileIdc));
        ms.WriteByte((byte)(compat >> 24)); ms.WriteByte((byte)(compat >> 16)); ms.WriteByte((byte)(compat >> 8)); ms.WriteByte((byte)compat);
        for (int i = 5; i >= 0; i--) ms.WriteByte((byte)(constraint >> (i * 8)));
        ms.WriteByte((byte)levelIdc);
        ms.WriteByte(0xF0); ms.WriteByte(0x00);   // min_spatial_segmentation_idc (reserved 1111 + 12 bits)
        ms.WriteByte(0xFC);                       // parallelismType (reserved 111111 + 2 bits)
        ms.WriteByte((byte)(0xFC | chromaFormatIdc));
        ms.WriteByte((byte)(0xF8 | bitDepthLumaMinus8));
        ms.WriteByte((byte)(0xF8 | bitDepthChromaMinus8));
        ms.WriteByte(0); ms.WriteByte(0);         // avgFrameRate
        ms.WriteByte((byte)((0 << 6) | ((maxSubLayersMinus1 + 1) << 3) | (temporalIdNesting << 2) | 3)); // constantFrameRate=0, numTemporalLayers, temporalIdNested, lengthSizeMinusOne=3
        ms.WriteByte(3);                          // numOfArrays
        WriteArray(ms, HEVC_VPS, vps);
        WriteArray(ms, HEVC_SPS, sps);
        WriteArray(ms, HEVC_PPS, pps);
        return ms.ToArray();

        static void WriteArray(MemoryStream ms, int type, byte[] nal)
        {
            ms.WriteByte((byte)(0x80 | type)); // array_completeness = 1
            ms.WriteByte(0); ms.WriteByte(1);  // numNalus
            ms.WriteByte((byte)(nal.Length >> 8)); ms.WriteByte((byte)nal.Length);
            ms.Write(nal);
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _d; private int _pos;
        public BitReader(byte[] d) => _d = d;
        public uint Read(int bits)
        {
            uint v = 0;
            for (int i = 0; i < bits; i++)
            {
                int byteIdx = _pos >> 3;
                int bit = byteIdx < _d.Length ? (_d[byteIdx] >> (7 - (_pos & 7))) & 1 : 0;
                v = (v << 1) | (uint)bit;
                _pos++;
            }
            return v;
        }
        public uint ReadUE()
        {
            int zeros = 0;
            while (Read(1) == 0 && zeros < 32) zeros++;
            return zeros == 0 ? 0 : (1u << zeros) - 1 + Read(zeros);
        }
    }
}
