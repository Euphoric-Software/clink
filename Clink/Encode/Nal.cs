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

}

/// <summary>MSB-first bit reader shared by the H.264/HEVC parameter-set and AV1 sequence-header parsers. Reads past the end return 0.</summary>
internal sealed class BitReader
{
    private readonly byte[] _d; private int _pos;
    public BitReader(byte[] d) => _d = d;
    public int Position => _pos;
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
    /// <summary>H.26x Exp-Golomb ue(v).</summary>
    public uint ReadUE()
    {
        int zeros = 0;
        while (Read(1) == 0 && zeros < 32) zeros++;
        return zeros == 0 ? 0 : (1u << zeros) - 1 + Read(zeros);
    }
    /// <summary>AV1 uvlc().</summary>
    public uint ReadUvlc()
    {
        int zeros = 0;
        while (Read(1) == 0 && zeros < 32) zeros++;
        if (zeros >= 32) return uint.MaxValue;
        return Read(zeros) + (1u << zeros) - 1;
    }
}

/// <summary>
/// AV1 helpers (experimental): walks the OBUs of a temporal unit in the low-overhead bitstream format (every OBU carries
/// obu_size), finds keyframes and the Sequence Header OBU, strips what must not be stored in an MP4 sample, and builds the
/// AV1CodecConfigurationRecord ('av1C') from the Sequence Header.
/// </summary>
internal static class Av1
{
    public const int OBU_SEQUENCE_HEADER = 1, OBU_TEMPORAL_DELIMITER = 2, OBU_FRAME_HEADER = 3, OBU_TILE_GROUP = 4,
        OBU_METADATA = 5, OBU_FRAME = 6, OBU_REDUNDANT_FRAME_HEADER = 7, OBU_TILE_LIST = 8, OBU_PADDING = 15;

    /// <summary>One OBU in a temporal unit: where it starts and how long it is (header + size field + payload), its type and where the payload is.</summary>
    public readonly record struct Obu(int Offset, int Length, int Type, int PayloadOffset, int PayloadLength, bool HasSize, bool HasExtension);

    /// <summary>What the Sequence Header says; the fields av1C needs plus the coded size.</summary>
    public readonly record struct SequenceInfo(int Profile, int LevelIdx, int Tier, bool HighBitDepth, bool TwelveBit, bool Monochrome,
        int SubsamplingX, int SubsamplingY, int ChromaSamplePosition, int MaxWidth, int MaxHeight);

    /// <summary>
    /// Splits a temporal unit into OBUs. Returns false with a reason when the bytes are not a valid OBU sequence (forbidden bit,
    /// obu_size past the end, truncated header). An OBU without obu_size is accepted only as the last one.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> tu, List<Obu> obus, out string? error)
    {
        error = null;
        int i = 0;
        while (i < tu.Length)
        {
            int start = i;
            byte h = tu[i++];
            if ((h & 0x80) != 0) { error = $"forbidden bit set at byte {start}"; return false; }
            int type = (h >> 3) & 0xF;
            bool ext = (h & 0x04) != 0;
            bool hasSize = (h & 0x02) != 0;
            if (ext) { if (i >= tu.Length) { error = $"truncated extension header at byte {start}"; return false; } i++; }
            int payloadLen;
            if (hasSize)
            {
                if (!TryReadLeb128(tu, ref i, out ulong size)) { error = $"bad obu_size at byte {start}"; return false; }
                if (size > (ulong)(tu.Length - i)) { error = $"obu_size {size} runs past the end at byte {start}"; return false; }
                payloadLen = (int)size;
            }
            else payloadLen = tu.Length - i;
            obus.Add(new Obu(start, i + payloadLen - start, type, i, payloadLen, hasSize, ext));
            i += payloadLen;
        }
        return true;
    }

    private static bool TryReadLeb128(ReadOnlySpan<byte> d, ref int pos, out ulong value)
    {
        value = 0;
        for (int k = 0; k < 8; k++)
        {
            if (pos >= d.Length) return false;
            byte b = d[pos++];
            value |= (ulong)(b & 0x7F) << (7 * k);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    private static byte[] Leb128(int v)
    {
        var o = new List<byte>(4);
        uint u = (uint)v;
        do
        {
            byte b = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0) b |= 0x80;
            o.Add(b);
        } while (u != 0);
        return o.ToArray();
    }

    /// <summary>
    /// True when the temporal unit starts a random-access point: its first frame (OBU_FRAME / OBU_FRAME_HEADER) is a KEY_FRAME that is
    /// not show_existing_frame, or, when there is no frame header to look at, when it carries a Sequence Header OBU.
    /// Assumes reduced_still_picture_header = 0, which every video encoder uses.
    /// </summary>
    public static bool ContainsKeyframe(ReadOnlySpan<byte> tu)
    {
        var obus = new List<Obu>();
        if (!TryParse(tu, obus, out _)) return false;
        bool seq = false;
        foreach (var o in obus)
        {
            if (o.Type == OBU_SEQUENCE_HEADER) { seq = true; continue; }
            if (o.Type is OBU_FRAME or OBU_FRAME_HEADER && o.PayloadLength > 0)
            {
                byte b = tu[o.PayloadOffset];
                bool showExisting = (b & 0x80) != 0;   // uncompressed_header(): show_existing_frame f(1), frame_type f(2)
                int frameType = (b >> 5) & 3;
                return !showExisting && frameType == 0;  // KEY_FRAME
            }
        }
        return seq;
    }

    /// <summary>The first Sequence Header OBU in the temporal unit as a complete OBU (header + obu_size + payload), or null.</summary>
    public static byte[]? FindSequenceHeader(ReadOnlySpan<byte> tu)
    {
        var obus = new List<Obu>();
        if (!TryParse(tu, obus, out _)) return null;
        foreach (var o in obus) if (o.Type == OBU_SEQUENCE_HEADER) return WithSize(tu, o);
        return null;
    }

    /// <summary>
    /// Turns an encoder temporal unit into an ISOBMFF AV1 sample: temporal delimiters and padding are dropped (they must not be
    /// stored), every OBU gets an obu_size field. A temporal unit that does not parse is stored as-is.
    /// </summary>
    public static byte[] ToSample(ReadOnlySpan<byte> tu)
    {
        var obus = new List<Obu>();
        if (!TryParse(tu, obus, out _)) return tu.ToArray();
        bool clean = true;
        foreach (var o in obus) if (o.Type is OBU_TEMPORAL_DELIMITER or OBU_PADDING || !o.HasSize) { clean = false; break; }
        if (clean) return tu.ToArray();
        using var ms = new MemoryStream(tu.Length);
        foreach (var o in obus)
        {
            if (o.Type is OBU_TEMPORAL_DELIMITER or OBU_PADDING) continue;
            ms.Write(WithSize(tu, o));
        }
        return ms.ToArray();
    }

    private static byte[] WithSize(ReadOnlySpan<byte> tu, Obu o)
    {
        if (o.HasSize) return tu.Slice(o.Offset, o.Length).ToArray();
        int hdrLen = o.HasExtension ? 2 : 1;
        byte[] size = Leb128(o.PayloadLength);
        var r = new byte[hdrLen + size.Length + o.PayloadLength];
        r[0] = (byte)(tu[o.Offset] | 0x02);
        if (o.HasExtension) r[1] = tu[o.Offset + 1];
        size.CopyTo(r, hdrLen);
        tu.Slice(o.PayloadOffset, o.PayloadLength).CopyTo(r.AsSpan(hdrLen + size.Length));
        return r;
    }

    /// <summary>Parses sequence_header_obu() (AV1 spec 5.5) up to and including color_config().</summary>
    public static SequenceInfo ParseSequenceHeader(ReadOnlySpan<byte> payload)
    {
        var br = new BitReader(payload.ToArray());
        int profile = (int)br.Read(3);
        br.Read(1);                                   // still_picture
        bool reduced = br.Read(1) == 1;               // reduced_still_picture_header
        int levelIdx = 0, tier = 0;
        if (reduced)
        {
            levelIdx = (int)br.Read(5);
        }
        else
        {
            bool decoderModelInfo = false;
            int bufferDelayLength = 0;
            if (br.Read(1) == 1)                      // timing_info_present_flag
            {
                br.Read(32); br.Read(32);             // num_units_in_display_tick, time_scale
                if (br.Read(1) == 1) br.ReadUvlc();   // equal_picture_interval -> num_ticks_per_picture_minus_1
                decoderModelInfo = br.Read(1) == 1;
                if (decoderModelInfo)
                {
                    bufferDelayLength = (int)br.Read(5) + 1;
                    br.Read(32); br.Read(5); br.Read(5);
                }
            }
            bool initialDisplayDelay = br.Read(1) == 1;
            int opCount = (int)br.Read(5) + 1;
            for (int i = 0; i < opCount; i++)
            {
                br.Read(12);                          // operating_point_idc
                int lv = (int)br.Read(5);
                int tr = lv > 7 ? (int)br.Read(1) : 0;
                if (i == 0) { levelIdx = lv; tier = tr; }
                if (decoderModelInfo && br.Read(1) == 1) { br.Read(bufferDelayLength); br.Read(bufferDelayLength); br.Read(1); }
                if (initialDisplayDelay && br.Read(1) == 1) br.Read(4);
            }
        }
        int wBits = (int)br.Read(4) + 1, hBits = (int)br.Read(4) + 1;
        int maxW = (int)br.Read(wBits) + 1, maxH = (int)br.Read(hBits) + 1;
        bool frameIdNumbers = !reduced && br.Read(1) == 1;
        if (frameIdNumbers) { br.Read(4); br.Read(3); }
        br.Read(1); br.Read(1); br.Read(1);           // use_128x128_superblock, enable_filter_intra, enable_intra_edge_filter
        if (!reduced)
        {
            br.Read(1); br.Read(1); br.Read(1); br.Read(1); // enable_interintra_compound, enable_masked_compound, enable_warped_motion, enable_dual_filter
            bool orderHint = br.Read(1) == 1;
            if (orderHint) { br.Read(1); br.Read(1); } // enable_jnt_comp, enable_ref_frame_mvs
            int forceSct = br.Read(1) == 1 ? 2 : (int)br.Read(1);
            if (forceSct > 0) { if (br.Read(1) == 0) br.Read(1); } // seq_choose_integer_mv ? SELECT : seq_force_integer_mv
            if (orderHint) br.Read(3);                // order_hint_bits_minus_1
        }
        br.Read(1); br.Read(1); br.Read(1);           // enable_superres, enable_cdef, enable_restoration
        // color_config()
        bool hbd = br.Read(1) == 1;
        bool twelve = profile == 2 && hbd && br.Read(1) == 1;
        bool mono = profile != 1 && br.Read(1) == 1;
        int cp = 2, tc = 2, mc = 2;
        if (br.Read(1) == 1) { cp = (int)br.Read(8); tc = (int)br.Read(8); mc = (int)br.Read(8); }
        int ssx, ssy, csp = 0;
        if (mono) { br.Read(1); ssx = ssy = 1; }
        else if (cp == 1 && tc == 13 && mc == 0) { ssx = ssy = 0; }
        else
        {
            br.Read(1);                               // color_range
            if (profile == 0) { ssx = ssy = 1; }
            else if (profile == 1) { ssx = ssy = 0; }
            else if (twelve) { ssx = (int)br.Read(1); ssy = ssx == 1 ? (int)br.Read(1) : 0; }
            else { ssx = 1; ssy = 0; }
            if (ssx == 1 && ssy == 1) csp = (int)br.Read(2);
        }
        return new SequenceInfo(profile, levelIdx, tier, hbd, twelve, mono, ssx, ssy, csp, maxW, maxH);
    }

    /// <summary>
    /// AV1CodecConfigurationRecord: marker=1, version=1, seq_profile / seq_level_idx_0 / seq_tier_0 / high_bitdepth / twelve_bit /
    /// monochrome / chroma_subsampling_x / chroma_subsampling_y / chroma_sample_position from the Sequence Header,
    /// initial_presentation_delay_present=0, configOBUs = the Sequence Header OBU.
    /// </summary>
    public static byte[] BuildAv1C(byte[] sequenceHeaderObu)
    {
        var obus = new List<Obu>();
        if (!TryParse(sequenceHeaderObu, obus, out string? err) || obus.Count == 0 || obus[0].Type != OBU_SEQUENCE_HEADER)
            throw new InvalidOperationException("AV1: not a Sequence Header OBU" + (err != null ? $" ({err})" : ""));
        var o = obus[0];
        var info = ParseSequenceHeader(sequenceHeaderObu.AsSpan(o.PayloadOffset, o.PayloadLength));
        byte[] cfg = WithSize(sequenceHeaderObu, o);
        var r = new byte[4 + cfg.Length];
        r[0] = 0x81;                                                   // marker | version 1
        r[1] = (byte)((info.Profile << 5) | (info.LevelIdx & 0x1F));
        r[2] = (byte)((info.Tier << 7) | ((info.HighBitDepth ? 1 : 0) << 6) | ((info.TwelveBit ? 1 : 0) << 5) | ((info.Monochrome ? 1 : 0) << 4)
                    | (info.SubsamplingX << 3) | (info.SubsamplingY << 2) | (info.ChromaSamplePosition & 3));
        r[3] = 0;                                                      // reserved, initial_presentation_delay_present = 0, reserved
        cfg.CopyTo(r, 4);
        return r;
    }

    public static string Describe(SequenceInfo s) =>
        $"profile {s.Profile} level_idx {s.LevelIdx} tier {s.Tier} {(s.TwelveBit ? 12 : s.HighBitDepth ? 10 : 8)}-bit {(s.Monochrome ? "mono" : s.SubsamplingX == 1 && s.SubsamplingY == 1 ? "4:2:0" : s.SubsamplingX == 1 ? "4:2:2" : "4:4:4")} {s.MaxWidth}x{s.MaxHeight}";
}
