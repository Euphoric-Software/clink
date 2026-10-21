using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Clink.Native;

namespace Clink.Capture;

/// <summary>GPU compute pass: captured RGB (SDR BGRA8 or HDR scRGB FP16) → NV12 with BT.709, scaling and HDR tone mapping.</summary>
internal sealed unsafe class FrameConverter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public uint DstW, DstH;
        public uint IsHdr;
        public float SdrScale;
        public float Knee;
        public float Peak;
        public uint FullRange;
        public uint Pad;
    }

    private readonly D3DDevice _d3d;
    private ID3D11ComputeShader* _shader;
    private ID3D11Buffer* _cb;
    private ID3D11SamplerState* _sampler;

    private ID3D11ShaderResourceView* _srv;
    private nint _srvTexture;

    private readonly Dictionary<(nint tex, uint sub), (nint y, nint uv)> _uavs = new();

    public uint OutputWidth { get; }
    public uint OutputHeight { get; }
    public bool IsHdr { get; set; }
    public float SdrWhiteNits { get; set; } = 80f;
    public float Knee { get; set; } = 0.75f;
    public float Peak { get; set; } = 4f;

    public FrameConverter(D3DDevice d3d, uint outW, uint outH)
    {
        _d3d = d3d;
        OutputWidth = outW;
        OutputHeight = outH;

        byte[] src;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Convert.hlsl")!)
        {
            src = new byte[s.Length];
            s.ReadExactly(src);
        }
        ID3DBlob* code = null, errors = null;
        HRESULT hr = PInvoke.D3DCompile(src, "Convert.hlsl", null, null, "main", "cs_5_0", PInvoke.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (hr.Failed)
        {
            string msg = errors != null ? Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize()) : hr.ToString();
            ComUtil.Release(ref errors);
            throw new InvalidOperationException("Shader compile failed: " + msg);
        }
        ComUtil.Release(ref errors);
        try
        {
            ID3D11ComputeShader* cs;
            d3d.Device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), null, &cs).Check("CreateComputeShader");
            _shader = cs;
        }
        finally { ComUtil.Release(ref code); }

        var bd = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(Params),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* cb;
        d3d.Device->CreateBuffer(&bd, null, &cb).Check("CreateBuffer(cb)");
        _cb = cb;

        var sd = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samp;
        d3d.Device->CreateSamplerState(&sd, &samp).Check("CreateSamplerState");
        _sampler = samp;
    }

    /// <summary>Must be called with the context lock held. Converts <paramref name="srcTex"/> into the NV12 texture (or array slice).</summary>
    public void Convert(ID3D11Texture2D* srcTex, ID3D11Texture2D* dstNv12, uint dstSubresource)
    {
        var ctx = _d3d.Context;

        if (_srv == null || _srvTexture != (nint)srcTex)
        {
            ComUtil.Release(ref _srv);
            ID3D11ShaderResourceView* srv;
            _d3d.Device->CreateShaderResourceView((ID3D11Resource*)srcTex, null, &srv).Check("CreateShaderResourceView");
            _srv = srv;
            _srvTexture = (nint)srcTex;
        }

        var (uavY, uavUV) = GetUavs(dstNv12, dstSubresource);

        D3D11_MAPPED_SUBRESOURCE mapped;
        ctx->Map((ID3D11Resource*)_cb, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped).Check("Map(cb)");
        *(Params*)mapped.pData = new Params
        {
            DstW = OutputWidth,
            DstH = OutputHeight,
            IsHdr = IsHdr ? 1u : 0u,
            SdrScale = 80f / Math.Max(1f, SdrWhiteNits),
            Knee = Knee,
            Peak = Peak,
            FullRange = 0,
        };
        ctx->Unmap((ID3D11Resource*)_cb, 0);

        ID3D11Buffer* cb = _cb;
        ID3D11ShaderResourceView* srvp = _srv;
        ID3D11SamplerState* sp = _sampler;
        var uavs = stackalloc ID3D11UnorderedAccessView*[2] { (ID3D11UnorderedAccessView*)uavY, (ID3D11UnorderedAccessView*)uavUV };
        var counts = stackalloc uint[2] { uint.MaxValue, uint.MaxValue };

        ctx->CSSetShader(_shader, null, 0);
        ctx->CSSetConstantBuffers(0, 1, &cb);
        ctx->CSSetShaderResources(0, 1, &srvp);
        ctx->CSSetSamplers(0, 1, &sp);
        ctx->CSSetUnorderedAccessViews(0, 2, uavs, counts);
        ctx->Dispatch((OutputWidth / 2 + 7) / 8, (OutputHeight / 2 + 7) / 8, 1);

        // Unbind so the encoder can consume the texture and the capture can overwrite the source.
        var nullUavs = stackalloc ID3D11UnorderedAccessView*[2] { null, null };
        ID3D11ShaderResourceView* nullSrv = null;
        ctx->CSSetUnorderedAccessViews(0, 2, nullUavs, counts);
        ctx->CSSetShaderResources(0, 1, &nullSrv);
    }

    private (nint, nint) GetUavs(ID3D11Texture2D* tex, uint subresource)
    {
        var key = ((nint)tex, subresource);
        if (_uavs.TryGetValue(key, out var found)) return found;

        D3D11_TEXTURE2D_DESC desc;
        tex->GetDesc(&desc);

        ID3D11UnorderedAccessView* y = CreatePlaneUav(tex, desc, subresource, DXGI_FORMAT.DXGI_FORMAT_R8_UNORM);
        ID3D11UnorderedAccessView* uv = CreatePlaneUav(tex, desc, subresource, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM);
        var entry = ((nint)y, (nint)uv);
        _uavs[key] = entry;
        return entry;
    }

    private ID3D11UnorderedAccessView* CreatePlaneUav(ID3D11Texture2D* tex, D3D11_TEXTURE2D_DESC desc, uint subresource, DXGI_FORMAT format)
    {
        var d = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = format };
        if (desc.ArraySize > 1)
        {
            d.ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2DARRAY;
            d.Texture2DArray.MipSlice = 0;
            d.Texture2DArray.FirstArraySlice = subresource;
            d.Texture2DArray.ArraySize = 1;
        }
        else
        {
            d.ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D;
            d.Texture2D.MipSlice = 0;
        }
        ID3D11UnorderedAccessView* uav;
        _d3d.Device->CreateUnorderedAccessView((ID3D11Resource*)tex, &d, &uav).Check($"CreateUnorderedAccessView({format})");
        return uav;
    }

    public void Dispose()
    {
        foreach (var (y, uv) in _uavs.Values)
        {
            ((ID3D11UnorderedAccessView*)y)->Release();
            ((ID3D11UnorderedAccessView*)uv)->Release();
        }
        _uavs.Clear();
        ComUtil.Release(ref _srv);
        ComUtil.Release(ref _sampler);
        ComUtil.Release(ref _cb);
        ComUtil.Release(ref _shader);
    }
}
