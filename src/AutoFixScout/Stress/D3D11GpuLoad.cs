using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AutoFixScout.Stress;

/// <summary>
/// Graphics card load for the stress test. Self-contained so it can be replaced: a D3D11 compute shader (heavy ALU loop)
/// is dispatched repeatedly on the adapter with the most dedicated video memory (DXGI). Each batch is short (a few
/// milliseconds per dispatch, then a wait for the GPU) so Windows never resets the card for taking too long and the
/// desktop stays responsive. Needs no setting changes. Packages: Vortice.Direct3D11 and Vortice.D3DCompiler (MIT).
/// </summary>
internal sealed class D3D11GpuLoad : IGpuLoad
{
    private const string Shader = @"
RWStructuredBuffer<float> o : register(u0);
[numthreads(256,1,1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float a = id.x * 0.0001f + 0.5f, b = a + 1.1f, c = a * 0.5f + 2.3f, d = 0.7f;
    [loop] for (uint i = 0; i < 1024; i++)
    {
        a = frac(mad(a, b, c));
        b = frac(mad(b, c, d));
        c = frac(mad(c, d, a));
        d = frac(mad(d, a, b));
        a = frac(mad(a, d, c));
        b = frac(mad(b, a, d));
        c = frac(mad(c, b, a));
        d = frac(mad(d, c, b));
    }
    o[id.x] = a + b + c + d;
}";

    private const int Groups = 2048;      // 2048 * 256 threads per dispatch
    private const int DispatchesPerBatch = 6;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _buffer;
    private ID3D11UnorderedAccessView? _uav;
    private ID3D11Query? _query;
    private Thread? _thread;
    private volatile bool _stop;

    public string? AdapterName { get; private set; }

    public bool TryInitialize(out string? reason)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? best = null;
            ulong bestMemory = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                var d = adapter.Description1;
                var software = (d.Flags & AdapterFlags.Software) != 0;
                var memory = (ulong)d.DedicatedVideoMemory;
                if (!software && memory >= bestMemory) { best?.Dispose(); best = adapter; bestMemory = memory; AdapterName = d.Description; }
                else adapter.Dispose();
            }
            if (best == null) { reason = "no graphics adapter was found"; return false; }
            using (best)
            {
                var r = D3D11.D3D11CreateDevice(best, DriverType.Unknown, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_0 }, out _device, out _, out _context);
                if (r.Failure || _device == null || _context == null) { reason = "Direct3D 11 could not be started"; return false; }
            }
            var compile = Compiler.Compile(Shader, "main", "stress.hlsl", "cs_5_0", out var blob, out var errors);
            if (compile.Failure || blob == null) { reason = "the load program could not be prepared"; errors?.Dispose(); return false; }
            using (blob) _shader = _device.CreateComputeShader(blob.AsBytes());
            var elements = Groups * 256;
            _buffer = _device.CreateBuffer(new BufferDescription((uint)(elements * 4), BindFlags.UnorderedAccess,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4));
            _uav = _device.CreateUnorderedAccessView(_buffer, new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)elements },
            });
            _query = _device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = "the graphics load could not start (" + ex.GetType().Name + ")";
            ReleaseAll();
            return false;
        }
    }

    public void Start()
    {
        if (_context == null) throw new InvalidOperationException("not initialized");
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "AutoFixScout GPU stress" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            var ctx = _context!;
            ctx.CSSetShader(_shader);
            ctx.CSSetUnorderedAccessView(0, _uav);
            while (!_stop)
            {
                for (var i = 0; i < DispatchesPerBatch; i++) ctx.Dispatch(Groups, 1, 1);
                ctx.End(_query);
                ctx.Flush();
                // Wait for the batch so the queue stays short and a stop takes effect within one batch.
                var spin = 0;
                while (!_stop)
                {
                    var r = ctx.GetData(_query!, out int done);
                    if (r && done != 0) break;
                    Thread.Sleep(spin++ < 20 ? 0 : 1);
                }
            }
        }
        catch (Exception) { /* the card went away or was reset; the test goes on without load */ }
    }

    public bool Stop(TimeSpan wait)
    {
        _stop = true;
        return _thread == null || !_thread.IsAlive || _thread.Join(wait);
    }

    private void ReleaseAll()
    {
        _query?.Dispose(); _uav?.Dispose(); _buffer?.Dispose(); _shader?.Dispose();
        try { _context?.ClearState(); _context?.Flush(); } catch (Exception) { }
        _context?.Dispose(); _device?.Dispose();
        _query = null; _uav = null; _buffer = null; _shader = null; _context = null; _device = null;
    }

    public void Dispose()
    {
        Stop(TimeSpan.FromSeconds(3));
        ReleaseAll();
    }
}
