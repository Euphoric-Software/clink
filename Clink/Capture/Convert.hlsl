// Clink: RGB (SDR sRGB or HDR scRGB) -> NV12 (BT.709) compute shader.
// One thread handles a 2x2 block of output pixels: 4 luma samples + 1 chroma sample.

cbuffer Params : register(b0)
{
    uint2  dstSize;    // NV12 output size (even)
    uint   isHdr;      // 1 = source is scRGB linear FP16 (1.0 = 80 nits)
    float  sdrScale;   // multiplier that maps the display's SDR white level to 1.0
    float  knee;       // tone-map: values below this are untouched (0..1)
    float  peak;       // tone-map: informational, shoulder reaches ~1.0 by this value
    uint   fullRange;  // 0 = limited (16..235), 1 = full range
    uint   _pad;
};

Texture2D<float4>   src  : register(t0);
SamplerState        samp : register(s0);
RWTexture2D<float>  dstY : register(u0);   // R8_UNORM view of NV12 plane 0
RWTexture2D<float2> dstUV: register(u1);   // R8G8_UNORM view of NV12 plane 1

float3 LinearToSrgb(float3 c)
{
    c = max(c, 0.0);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return (c <= 0.0031308) ? lo : hi;
}

// Hue-preserving soft shoulder: identity up to `knee`, then an exponential roll-off
// that asymptotically reaches 1.0. C1-continuous at the knee.
float3 ToneMap(float3 c)
{
    float m = max(c.r, max(c.g, c.b));
    if (m <= knee) return c;
    float w = 1.0 - knee;
    float y = knee + w * (1.0 - exp(-(m - knee) / w));
    return c * (y / m);
}

// Fetch one source pixel (already in display-referred, gamma-encoded sRGB 0..1).
float3 FetchSrgb(uint2 p)
{
    float2 uv = (float2(p) + 0.5) / float2(dstSize);
    float3 c = src.SampleLevel(samp, uv, 0).rgb;
    if (isHdr)
    {
        c = max(c * sdrScale, 0.0);   // scRGB linear -> SDR-white-relative linear
        c = ToneMap(c);
        c = LinearToSrgb(c);
    }
    return saturate(c);
}

float Luma709(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint2 base = id.xy * 2;
    if (base.x >= dstSize.x || base.y >= dstSize.y) return;

    float3 c00 = FetchSrgb(base + uint2(0, 0));
    float3 c10 = FetchSrgb(base + uint2(1, 0));
    float3 c01 = FetchSrgb(base + uint2(0, 1));
    float3 c11 = FetchSrgb(base + uint2(1, 1));

    float y00 = Luma709(c00), y10 = Luma709(c10), y01 = Luma709(c01), y11 = Luma709(c11);

    float3 avg = (c00 + c10 + c01 + c11) * 0.25;
    float yAvg = Luma709(avg);
    float cb = (avg.b - yAvg) / 1.8556;
    float cr = (avg.r - yAvg) / 1.5748;

    float yScale, yOff, cScale;
    if (fullRange) { yScale = 255.0; yOff = 0.0;  cScale = 255.0; }
    else           { yScale = 219.0; yOff = 16.0; cScale = 224.0; }

    dstY[base + uint2(0, 0)] = (yOff + y00 * yScale) / 255.0;
    dstY[base + uint2(1, 0)] = (yOff + y10 * yScale) / 255.0;
    dstY[base + uint2(0, 1)] = (yOff + y01 * yScale) / 255.0;
    dstY[base + uint2(1, 1)] = (yOff + y11 * yScale) / 255.0;
    dstUV[id.xy] = float2(128.0 + cb * cScale, 128.0 + cr * cScale) / 255.0;
}
