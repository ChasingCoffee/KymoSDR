// Tier 3 - GPU mesh based 3D panadapter surface (experimental)
//
// Replaces the per-column DrawLine curtain painting of DrawPanadapter3DHistoryDX2D with a
// real Direct3D11 triangle sheet rendered straight into the shared swapchain backbuffer
// BEFORE the D2D frame begins. The D2D line renderer stays untouched and remains the
// permanent fallback (GPU fallback rule 1); this path is only taken when:
//   - GpuMeshEnabled is set by the user
//   - render path is Hardware (WARP would just be slower CPU rasterisation)
//   - the mesh pipeline is healthy (any failure silently falls back for the frame)
//
// Geometry mirrors the Aether DssRenderer math already used by the D2D path exactly:
// per depth row tS: width frac 1-tS*(1-backW), inset, baseline bottomY-tS*depthSpan,
// foreshortened ridge frontRidge*rowWidthFrac, floor lift pow(s,zCurve).
// Heights are streamed as an R32Float texture (one texel per column x depth row) and the
// static vertex buffer is just a UV grid, so the per-frame CPU cost is one small texture
// update. Colour = palette texture lookup by raw strength (built per frame from the same
// SelectSurfaceColour sources as the D2D path), plus horizontal slope shading and linear
// depth haze to match the established look.
using System;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.Direct2D1;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Thetis
{
    partial class Display
    {
        #region GPU mesh fields

        private const int MeshPaletteSize = 256;

        private static bool _meshShadersBuilt;
        private static bool _meshFailedLogged;
        private static ID3D11RenderTargetView _meshRTV;
        private static ID3D11VertexShader _meshVS;
        private static ID3D11PixelShader _meshPS;
        private static ID3D11InputLayout _meshIL;
        private static ID3D11Buffer _meshVB;          // static UV grid
        private static ID3D11Buffer _meshIB;          // static index buffer
        private static ID3D11Buffer _meshCB;          // constants
        private static ID3D11Texture2D _meshHeightTex;
        private static ID3D11ShaderResourceView _meshHeightSRV;
        private static ID3D11Texture2D _meshPaletteTex;
        private static ID3D11ShaderResourceView _meshPaletteSRV;
        private static ID3D11BlendState _meshBlend;
        private static ID3D11RasterizerState _meshRS;
        private static ID3D11SamplerState _sampPoint;
        private static ID3D11SamplerState _sampLinear;

        // scissored-clear helpers (repaint the plot strip over a pre-drawn skin image)
        private static ID3D11VertexShader _meshClearVS;
        private static ID3D11PixelShader _meshClearPS;
        private static ID3D11Buffer _meshClearVB;
        private static ID3D11Buffer _meshClearIB;
        private static ID3D11BlendState _meshBlendOpaque;   // no blending = overwrite
        private static ID3D11RasterizerState _meshRSScissor;
        private static int _meshRows = -1;            // built grid dimensions
        private static int _meshCols = -1;
        private static float[] _meshHeightScratch;    // rows*cols strengths

        // captured by DrawPanadapterDX2D each frame, consumed pre-BeginDraw next frame
        private struct MeshFrameParams
        {
            public bool Valid;
            public float W, PlotH, TargetH, Shift;
            public int Cols, Decimation, GridMin, GridMax;
        }
        private static MeshFrameParams _meshParams;

        #endregion

        #region GPU mesh public control

        /// <summary>Experimental Tier 3 GPU mesh 3D surface toggle (session only).</summary>
        public static bool GpuMeshEnabled { get; set; }

        private static void CaptureMeshFrameParams(int nVerticalShift, int W, int H, int rx, int nDecimatedWidth, int local_Decimation, int grid_min, int grid_max)
        {
            if (rx != 1) return;
            _meshParams.W = W;
            _meshParams.PlotH = H;
            _meshParams.TargetH = displayTargetHeight;
            _meshParams.Shift = nVerticalShift;
            _meshParams.Cols = nDecimatedWidth;
            _meshParams.Decimation = local_Decimation;
            _meshParams.GridMin = grid_min;
            _meshParams.GridMax = grid_max;
            _meshParams.Valid = true;
        }

        #endregion

        #region GPU mesh HLSL

        private const string MESH_HLSL = @"
            cbuffer MeshCB : register(b0)
            {
                float CB_W;         // plot width px
                float CB_TargetH;   // full backbuffer height px
                float CB_BottomY;   // absolute bottom of plot
                float CB_DepthSpan; // total baseline rise
                float CB_FrontRidge;// max ridge height at front
                float CB_BackW;     // back width fraction
                float CB_ZCurve;    // floor lift exponent
                float CB_Haze;      // haze strength
                float3 CB_Background;
                float CB_TexelX;    // 1/cols for neighbour taps
            };

            Texture2D HeightTex : register(t0);
            Texture2D PaletteTex : register(t1);
            SamplerState PointSamp : register(s0);
            SamplerState LinearSamp : register(s1);

            struct PSIN
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            PSIN vs_main(float2 uv : POSITION)
            {
                PSIN o;
                float v = uv.y;                                  // 0 = front row, 1 = back row
                float rwf = 1.0 - v * (1.0 - CB_BackW);          // row width fraction
                float inset = CB_W * (1.0 - rwf) * 0.5;
                float h = HeightTex.SampleLevel(PointSamp, float2(uv.x, v), 0).r;
                float lift = pow(max(h, 0.0), CB_ZCurve);
                float x = inset + uv.x * (CB_W - 2.0 * inset);
                float baseline = CB_BottomY - v * CB_DepthSpan;
                float y = baseline - lift * CB_FrontRidge * rwf; // uniform perspective scaling
                o.pos = float4(x / CB_W * 2.0 - 1.0, 1.0 - y / CB_TargetH * 2.0, 0.0, 1.0);
                o.uv = uv;
                return o;
            }

            float4 ps_main(PSIN i) : SV_Target
            {
                float s = HeightTex.SampleLevel(PointSamp, i.uv, 0).r;
                // horizontal slope shading (Aether kSlopeGain=0.55, shade 0.68-1.32),
                // matching the D2D path's per-column shade so the look carries over
                float hl = HeightTex.SampleLevel(PointSamp, i.uv - float2(CB_TexelX, 0.0), 0).r;
                float hr = HeightTex.SampleLevel(PointSamp, i.uv + float2(CB_TexelX, 0.0), 0).r;
                float slope = pow(max(hl, 0.0), CB_ZCurve) - pow(max(hr, 0.0), CB_ZCurve);
                float shade = clamp(1.0 + 0.55 * slope, 0.68, 1.32);

                float v = i.uv.y;
                float dim = 0.72 + 0.28 * (1.0 - v);             // depth dimming
                float haze = v * CB_Haze;                        // linear fog toward background

                float4 col = PaletteTex.SampleLevel(LinearSamp, float2(saturate(s), 0.5), 0);
                col.rgb *= dim * shade;
                col.rgb = lerp(col.rgb, CB_Background, saturate(haze));
                float alpha = 1.0 - v * 0.15;                    // matches D2D fill alpha fade
                return float4(col.rgb, alpha);
            }

            // clear helpers: scissored fullscreen quad that repaints only the plot
            // strip, so a skin background image drawn by D2D beforehand survives
            float4 vs_clear(float2 uv : POSITION) : SV_Position
            {
                return float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            }

            float4 ps_clear() : SV_Target
            {
                return float4(CB_Background, 1.0);
            }
            ";

        #endregion

        #region GPU mesh implementation

        private static void ReleaseGpuMeshDeviceObjects()
        {
            _meshRTV?.Dispose(); _meshRTV = null;
            _meshHeightSRV?.Dispose(); _meshHeightSRV = null;
            _meshHeightTex?.Dispose(); _meshHeightTex = null;
            _meshPaletteSRV?.Dispose(); _meshPaletteSRV = null;
            _meshPaletteTex?.Dispose(); _meshPaletteTex = null;
            _meshIL?.Dispose(); _meshIL = null;
            _meshVS?.Dispose(); _meshVS = null;
            _meshPS?.Dispose(); _meshPS = null;
            _meshVB?.Dispose(); _meshVB = null;
            _meshIB?.Dispose(); _meshIB = null;
            _meshCB?.Dispose(); _meshCB = null;
            _meshBlend?.Dispose(); _meshBlend = null;
            _meshRS?.Dispose(); _meshRS = null;
            _sampPoint?.Dispose(); _sampPoint = null;
            _sampLinear?.Dispose(); _sampLinear = null;
            _meshClearVS?.Dispose(); _meshClearVS = null;
            _meshClearPS?.Dispose(); _meshClearPS = null;
            _meshClearVB?.Dispose(); _meshClearVB = null;
            _meshClearIB?.Dispose(); _meshClearIB = null;
            _meshBlendOpaque?.Dispose(); _meshBlendOpaque = null;
            _meshRSScissor?.Dispose(); _meshRSScissor = null;
            _meshRows = -1; _meshCols = -1;
            _meshHeightScratch = null;
            _meshShadersBuilt = false;
        }

        /// <summary>Called from resize/shutdown paths alongside releaseGlowLayer().</summary>
        private static void ReleaseGpuMeshFrameState()
        {
            _meshRTV?.Dispose();
            _meshRTV = null;
            _meshParams.Valid = false;
        }

        private static bool BuildMeshPipeline(ID3D11Device device)
        {
            try
            {
                byte[] vsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "vs_main", "pan3dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] psBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_main", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshVS = device.CreateVertexShader(vsBytes, null);
                _meshPS = device.CreatePixelShader(psBytes);

                // clear variants (scissored quad repaint of the plot strip)
                byte[] cvsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "vs_clear", "pan3dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] cpsBytes = Vortice.D3DCompiler.Compiler.Compile(MESH_HLSL, "ps_clear", "pan3dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _meshClearVS = device.CreateVertexShader(cvsBytes, null);
                _meshClearPS = device.CreatePixelShader(cpsBytes);

                Vortice.Direct3D11.InputElementDescription[] layout = new Vortice.Direct3D11.InputElementDescription[]
                {
                    new Vortice.Direct3D11.InputElementDescription("POSITION", 0, Format.R32G32_Float, 0),
                };
                _meshIL = device.CreateInputLayout(layout, vsBytes);

                _meshCB = device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _meshBlend = device.CreateBlendState(Vortice.Direct3D11.BlendDescription.AlphaBlend);
                // winding ends up CCW in NDC (y-flip in the VS) - disable culling entirely
                _meshRS = device.CreateRasterizerState(new Vortice.Direct3D11.RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
                // samplers MUST be created and bound - without them every texture read returns 0
                _sampPoint = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
                _sampLinear = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

                // clear quad geometry (unit uv square) + opaque blend + scissor rasterizer
                float[] clearVerts = new float[] { 0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f };
                uint[] clearIdx = new uint[] { 0, 1, 2, 2, 1, 3 };
                _meshClearVB = device.CreateBuffer(clearVerts, new BufferDescription(
                    (uint)(clearVerts.Length * sizeof(float)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
                _meshClearIB = device.CreateBuffer(clearIdx, new BufferDescription(
                    (uint)(clearIdx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));
                _meshBlendOpaque = device.CreateBlendState(new Vortice.Direct3D11.BlendDescription());
                Vortice.Direct3D11.RasterizerDescription rsScissor =
                    new Vortice.Direct3D11.RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid);
                rsScissor.ScissorEnable = true;
                _meshRSScissor = device.CreateRasterizerState(rsScissor);
                _meshShadersBuilt = true;
                return true;
            }
            catch (Exception e)
            {
                Common.LogString("GPU mesh: shader build failed - " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                return false;
            }
        }

        private static bool EnsureMeshGrid(ID3D11Device device, int rows, int cols)
        {
            if (_meshRows == rows && _meshCols == cols && _meshVB != null && _meshIB != null && _meshHeightTex != null)
                return true;

            try
            {
                _meshVB?.Dispose(); _meshVB = null;
                _meshIB?.Dispose(); _meshIB = null;
                _meshHeightSRV?.Dispose(); _meshHeightSRV = null;
                _meshHeightTex?.Dispose(); _meshHeightTex = null;

                // UV grid: rows*cols vertices
                var verts = new float[rows * cols * 2];
                for (int r = 0; r < rows; r++)
                {
                    float v = r / (float)(rows - 1);
                    for (int c = 0; c < cols; c++)
                    {
                        int o = (r * cols + c) * 2;
                        verts[o] = c / (float)(cols - 1);
                        verts[o + 1] = v;
                    }
                }
                _meshVB = device.CreateBuffer(verts, new BufferDescription((uint)(verts.Length * sizeof(float)), BindFlags.VertexBuffer, ResourceUsage.Immutable));

                uint[] idx = new uint[(rows - 1) * (cols - 1) * 6];
                int ii = 0;
                for (int r = 0; r < rows - 1; r++)
                {
                    for (int c = 0; c < cols - 1; c++)
                    {
                        uint a = (uint)(r * cols + c);
                        uint b = a + 1;
                        uint d = a + (uint)cols;
                        uint e = d + 1;
                        idx[ii++] = a; idx[ii++] = d; idx[ii++] = b;
                        idx[ii++] = b; idx[ii++] = d; idx[ii++] = e;
                    }
                }
                _meshIB = device.CreateBuffer(idx, new BufferDescription((uint)(idx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));

                _meshHeightTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)cols,
                    Height = (uint)rows,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                _meshHeightSRV = device.CreateShaderResourceView(_meshHeightTex);

                if (_meshPaletteTex == null)
                {
                    _meshPaletteTex = device.CreateTexture2D(new Texture2DDescription()
                    {
                        Width = (uint)MeshPaletteSize,
                        Height = 1,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Dynamic,
                        BindFlags = BindFlags.ShaderResource,
                        CPUAccessFlags = CpuAccessFlags.Write,
                    });
                    _meshPaletteSRV = device.CreateShaderResourceView(_meshPaletteTex);
                }

                _meshRows = rows;
                _meshCols = cols;
                _meshHeightScratch = new float[rows * cols];
                return true;
            }
            catch (Exception e)
            {
                Common.LogString("GPU mesh: grid build failed (" + rows + "x" + cols + ") - " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                return false;
            }
        }

        private static bool EnsureMeshRTV(ID3D11Device device)
        {
            if (_meshRTV != null) return true;
            try
            {
                using (ID3D11Texture2D bb = _swapChain1.GetBuffer<ID3D11Texture2D>(0))
                    _meshRTV = device.CreateRenderTargetView(bb);
                return true;
            }
            catch (Exception e)
            {
                Common.LogString("GPU mesh: RTV creation failed - " + e.Message);
                return false;
            }
        }

        private struct MeshConstants
        {
            public float W, TargetH, BottomY, DepthSpan;   // 0-15
            public float FrontRidge, BackW, ZCurve, Haze;  // 16-31
            public float BgR, BgG, BgB, TexelX;            // 32-47 (float3 aligned to 16)
        }

        /// <summary>
        /// Draws the history surface as a GPU mesh into the swapchain backbuffer.
        /// Returns false when the frame should fall back to the D2D line renderer.
        /// Runs under _objDX2Lock BEFORE the D2D BeginDraw of the same frame; the D3D
        /// flush below guarantees queue ordering against the subsequent D2D pass.
        /// </summary>
        private static bool RenderGpuMesh3D()
        {
            if (!GpuMeshEnabled || m_eRenderPath != DXRenderPath.Hardware || _device == null || !_bDX2Setup)
                return false;
            if (!_pan3DEnabled || _3dHistoryBuffer == null || _3dHistoryCount < 3 || !_meshParams.Valid)
                return false;
            if (_paused_display || localMox(1)) return false;

            try
            {
                float[][] histBuf = _3dHistoryBuffer;
                int histHead = _3dHistoryHead;
                int histCount = _3dHistoryCount;

                int linesToDraw = Math.Min(histCount, _pan3DLineCount);
                if (linesToDraw < 3) return false;
                int rowCount = linesToDraw - 1; // front-most stored row is covered by the live trace

                int yRange = _meshParams.GridMax - _meshParams.GridMin;
                if (yRange <= 0 || _meshParams.Cols < 2) return false;

                ID3D11DeviceContext dc = _device.ImmediateContext;
                if (!_meshShadersBuilt && !BuildMeshPipeline(_device)) return false;
                if (!EnsureMeshRTV(_device)) return false;
                if (!EnsureMeshGrid(_device, rowCount, _meshParams.Cols)) return false;

                // ---- temporal phase (identical to the D2D path) ----
                float phase = 0f;
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    long interval = _pan3DWaterfallSync
                        ? (long)(getWaterfallLineIntervalMs(1) * 10000.0)
                        : _3dPushIntervalTicks;
                    if (interval < 10000) interval = 10000;
                    phase = (nowTicks - _3dLastPushTicks) / (float)interval;
                    if (phase < 0f) phase = 0f; else if (phase > 1f) phase = 1f;
                }

                // ---- stream heights (raw strengths; lift happens in the shaders) ----
                float[] scratch = _meshHeightScratch;
                int cols = _meshCols;
                for (int r = 0; r < rowCount; r++)
                {
                    int line = r + 1;
                    float fIdx = line - phase;
                    int i0 = (int)fIdx;
                    if (i0 < 0) i0 = 0;
                    float w = fIdx - i0;
                    int i1 = i0 + 1;
                    if (i1 > linesToDraw - 1) { i1 = i0; w = 0f; }

                    int idx0 = (histHead - 1 - i0 + Max3DHistoryLines * 2) % Max3DHistoryLines;
                    int idx1 = (histHead - 1 - i1 + Max3DHistoryLines * 2) % Max3DHistoryLines;
                    float[] f0 = histBuf[idx0];
                    float[] f1 = histBuf[idx1];
                    int rowOff = r * cols;

                    if (f0 == null || f0.Length < cols)
                    {
                        for (int c = 0; c < cols; c++) scratch[rowOff + c] = 0f;
                        continue;
                    }

                    if (w > 0.0001f && f1 != null && f1.Length >= cols)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            float dBm = f0[c] + (f1[c] - f0[c]) * w;
                            float s = (dBm - _meshParams.GridMin) / (float)yRange;
                            scratch[rowOff + c] = s < 0f ? 0f : (s > 1f ? 1f : s);
                        }
                    }
                    else
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            float s = (f0[c] - _meshParams.GridMin) / (float)yRange;
                            scratch[rowOff + c] = s < 0f ? 0f : (s > 1f ? 1f : s);
                        }
                    }
                }

                MappedSubresource mapped = dc.Map(_meshHeightTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    fixed (float* src = scratch)
                    {
                        int copyBytes = cols * sizeof(float);
                        byte* dst = (byte*)mapped.DataPointer;
                        for (int r = 0; r < rowCount; r++)
                            Buffer.MemoryCopy(src + r * cols, dst + r * mapped.RowPitch, copyBytes, copyBytes);
                    }
                }
                dc.Unmap((ID3D11Resource)_meshHeightTex, 0);

                // ---- palette texture (same colour selection rules as SelectSurfaceColour) ----
                BuildMeshPalette(dc, yRange);

                // ---- constants ----
                var cb = new MeshConstants
                {
                    W = _meshParams.W,
                    TargetH = _meshParams.TargetH,
                    BottomY = _meshParams.Shift + _meshParams.PlotH,
                    DepthSpan = _meshParams.PlotH * _pan3DDepth,
                    FrontRidge = _meshParams.PlotH * _pan3DRidgeHeight,
                    BackW = _pan3DPerspective,
                    ZCurve = Math.Max(0.05f, _pan3DZCurve),
                    Haze = _pan3DDepthFade,
                    BgR = m_cDX2_display_background_clear_colour.R,
                    BgG = m_cDX2_display_background_clear_colour.G,
                    BgB = m_cDX2_display_background_clear_colour.B,
                    TexelX = 1f / cols,
                };
                MappedSubresource cbMap = dc.Map((ID3D11Resource)_meshCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
                dc.Unmap((ID3D11Resource)_meshCB, 0);

                dc.IASetInputLayout(_meshIL);
                dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                // CRITICAL: bind the render target to the output-merger stage -
                // without this every fragment is discarded (clear works regardless)
                dc.OMSetRenderTargets(new[] { _meshRTV }, null);

                if (_bitmapBackground != null)
                {
                    // skin image present: draw it first via D2D so it stays visible
                    // around the mesh, then repaint ONLY the plot strip with a
                    // scissored opaque quad instead of a full-target clear
                    DrawSkinBackgroundPrepass();

                    dc.RSSetState(_meshRSScissor);
                    dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, (int)_meshParams.Shift,
                        (int)displayTargetWidth, (int)_meshParams.Shift + (int)_meshParams.PlotH) });
                    dc.OMSetBlendState(_meshBlendOpaque);
                    dc.VSSetShader(_meshClearVS);
                    dc.PSSetShader(_meshClearPS);
                    dc.VSSetConstantBuffer(0, _meshCB);   // ps_clear reads CB_Background
                    dc.IASetVertexBuffer(0, _meshClearVB, 8, 0);
                    dc.IASetIndexBuffer(_meshClearIB, Format.R32_UInt, 0);
                    dc.DrawIndexed(6, 0, 0);

                    // restore surface state
                    dc.VSSetShader(_meshVS);
                    dc.PSSetShader(_meshPS);
                    dc.OMSetBlendState(_meshBlend);
                    dc.RSSetState(_meshRS);
                    dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, 0,
                        (int)displayTargetWidth, (int)displayTargetHeight) });
                }
                else
                {
                    // no skin image: full clear to the configured background colour
                    dc.ClearRenderTargetView(_meshRTV, new Color4(
                        m_cDX2_display_background_clear_colour.R,
                        m_cDX2_display_background_clear_colour.G,
                        m_cDX2_display_background_clear_colour.B, 1f));
                }

                dc.IASetVertexBuffer(0, _meshVB, 8, 0);
                dc.IASetIndexBuffer(_meshIB, Format.R32_UInt, 0);
                dc.VSSetShader(_meshVS);
                dc.PSSetShader(_meshPS);
                dc.VSSetConstantBuffer(0, _meshCB);
                dc.PSSetConstantBuffer(0, _meshCB);
                dc.PSSetShaderResource(0, _meshHeightSRV);
                dc.PSSetShaderResource(1, _meshPaletteSRV);
                // the VS samples the height texture itself - needs its own SRV + sampler binding
                dc.VSSetShaderResource(0, _meshHeightSRV);
                dc.VSSetSamplers(0, new[] { _sampPoint });
                dc.PSSetSamplers(0, new[] { _sampPoint });
                dc.PSSetSamplers(1, new[] { _sampLinear });
                dc.OMSetBlendState(_meshBlend);
                dc.RSSetState(_meshRS);
                dc.RSSetViewport(new Viewport(0f, 0f, displayTargetWidth, displayTargetHeight));
                dc.DrawIndexed((uint)((rowCount - 1) * (cols - 1) * 6), 0, 0);
                dc.Flush();

                if (!_meshFailedLogged)
                {
                    _meshFailedLogged = true;
                    Common.LogString("GPU mesh surface active (" + rowCount + "x" + cols + ")");
                }
                return true;
            }
            catch (Exception e)
            {
                Common.LogString("GPU mesh render failed - falling back to D2D lines : " + e.Message);
                ReleaseGpuMeshDeviceObjects();
                ReleaseGpuMeshFrameState();
                return false;
            }
        }

        /// <summary>
        /// Draws the skin background image through D2D immediately before the mesh
        /// pass, replicating the aspect-ratio logic of the normal frame so the
        /// image stays visible around the mesh trapezoid.
        /// </summary>
        private static void DrawSkinBackgroundPrepass()
        {
            try
            {
                System.Numerics.Matrix3x2 t = _d2dRenderTarget.Transform;
                t.Translation = m_pixelShift;
                _d2dRenderTarget.Transform = t;

                System.Drawing.RectangleF rectDest;
                if (_maintain_background_aspectratio)
                {
                    float imageWidth = _bitmapBackground.PixelSize.Width;
                    float imageHeight = _bitmapBackground.PixelSize.Height;
                    float aspectRatio = imageWidth / imageHeight;
                    float targetAspectRatio = displayTargetWidth / displayTargetHeight;

                    if (aspectRatio > targetAspectRatio)
                    {
                        float scaledHeight = displayTargetWidth / aspectRatio;
                        rectDest = new System.Drawing.RectangleF(0, (displayTargetHeight - scaledHeight) / 2, displayTargetWidth, scaledHeight);
                    }
                    else
                    {
                        float scaledWidth = displayTargetHeight * aspectRatio;
                        rectDest = new System.Drawing.RectangleF((displayTargetWidth - scaledWidth) / 2, 0, scaledWidth, displayTargetHeight);
                    }
                }
                else
                {
                    rectDest = new System.Drawing.RectangleF(0, 0, displayTargetWidth, displayTargetHeight);
                }

                _d2dRenderTarget.BeginDraw();
                _d2dRenderTarget.DrawBitmap(_bitmapBackground,
                    new Vortice.RawRectF(rectDest.X, rectDest.Y, rectDest.Right, rectDest.Bottom),
                    1f, BitmapInterpolationMode.Linear, null);
                _d2dRenderTarget.EndDraw();
            }
            catch (Exception e)
            {
                Common.LogString("GPU mesh: background prepass failed - " + e.Message);
            }
        }

        /// <summary>Per-frame palette upload replicating SelectSurfaceColour priorities:
        /// waterfall sync > perceptual colormap > gradient > line colour brightness.</summary>
        private static void BuildMeshPalette(ID3D11DeviceContext dc, int yRange)
        {
            int grid_min = _meshParams.GridMin;

            // mirror the priority logic of DrawPanadapter3DHistoryDX2D
            bool useWaterfallSync = _pan3DWaterfallSync;
            float wfLowThreshold = waterfall_low_threshold;
            float wfHighThreshold = waterfall_high_threshold;
            if (rx1_waterfall_agc && !m_bRX1_spectrum_thresholds)
            {
                wfLowThreshold = _RX1waterfallPreviousMinValue;
                wfLowThreshold -= m_fWaterfallAGCOffsetRX1;
            }
            float wfRange = wfHighThreshold - wfLowThreshold;
            if (wfRange <= 0) useWaterfallSync = false;

            int colorMapIdx = _pan3DColorMap;
            bool useColormap = colorMapIdx > 0 && !useWaterfallSync;
            if (useColormap && _colormapLUT == null) BuildColormapLUT();

            const int gradPaletteSize = 64;
            System.Drawing.Color[] gradPalette = null;
            bool useGradient = !useWaterfallSync && m_bUseLinearGradient && console.SetupForm?.RX1GradPicker != null;
            if (useGradient)
            {
                try
                {
                    gradPalette = new System.Drawing.Color[gradPaletteSize];
                    for (int i = 0; i < gradPaletteSize; i++)
                    {
                        float t = (float)i / (gradPaletteSize - 1);
                        float dBm = grid_min + t * yRange;
                        gradPalette[i] = console.SetupForm.RX1GradPicker.GetColourForDBM(dBm);
                    }
                }
                catch
                {
                    useGradient = false;
                    gradPalette = null;
                }
            }

            MappedSubresource map = dc.Map(_meshPaletteTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                uint* row = (uint*)map.DataPointer;
                for (int i = 0; i < MeshPaletteSize; i++)
                {
                    float strength = i / (float)(MeshPaletteSize - 1);
                    float dBm = grid_min + strength * yRange;
                    int R, G, B;
                    if (useColormap)
                    {
                        int ci = (int)(strength * 255f);
                        int o = ((colorMapIdx - 1) * 256 + ci) * 3;
                        R = _colormapLUT[o]; G = _colormapLUT[o + 1]; B = _colormapLUT[o + 2];
                    }
                    else if (useWaterfallSync)
                    {
                        GetWaterfallColor(dBm, wfLowThreshold, wfHighThreshold, _rx1_color_scheme,
                            waterfall_low_color, _rx1_waterfall_grad, _rx1_waterfall_grad_ok, out R, out G, out B);
                    }
                    else if (useGradient)
                    {
                        int pIdx = (int)(strength * (gradPaletteSize - 1));
                        R = gradPalette[pIdx].R; G = gradPalette[pIdx].G; B = gradPalette[pIdx].B;
                    }
                    else
                    {
                        float bright = 0.25f + 0.75f * strength;
                        R = (int)(_pan3DLineColor.R * bright);
                        G = (int)(_pan3DLineColor.G * bright);
                        B = (int)(_pan3DLineColor.B * bright);
                    }
                    if (R < 0) R = 0; else if (R > 255) R = 255;
                    if (G < 0) G = 0; else if (G > 255) G = 255;
                    if (B < 0) B = 0; else if (B > 255) B = 255;
                    // B8G8R8A8_UNorm memory order is [B,G,R,A]; as a little-endian
                    // uint that is A<<24 | R<<16 | G<<8 | B
                    row[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | (uint)B;
                }
            }
            dc.Unmap((ID3D11Resource)_meshPaletteTex, 0);
        }

        #endregion
    }
}






