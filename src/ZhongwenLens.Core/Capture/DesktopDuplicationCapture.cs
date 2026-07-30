using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZhongwenLens.Core.Capture;

/// <summary>
/// Desktop capture via DXGI Desktop Duplication.
/// </summary>
/// <remarks>
/// <para>
/// Reads the composited output of each monitor straight from the GPU, which is what makes
/// exclusive-fullscreen games and hardware-decoded video capture correctly — both come back
/// black under GDI (DESIGN.md §3.1).
/// </para>
/// <para>
/// Chosen over <c>Windows.Graphics.Capture</c> because it needs no WinRT capture-item COM
/// interop and no frame pool or session lifecycle; acquire a frame, copy it, done. WGC's
/// advantages — per-window capture, HDR — aren't needed for a region snip.
/// </para>
/// <para>
/// <b>The catch:</b> duplication only produces a frame when the desktop actually changes, so a
/// completely static screen times out. Each monitor is therefore retried briefly, and anything
/// that still fails — a timeout, a rotated display, a protected surface, a lost duplication
/// after a mode change — falls back to a GDI blit of just that monitor. The result is always a
/// complete desktop image, never a partial one.
/// </para>
/// </remarks>
public sealed class DesktopDuplicationCapture : IScreenCapture
{
    /// <summary>Per-attempt wait for a frame. Several short waits beat one long one.</summary>
    private const int FrameTimeoutMs = 45;

    /// <summary>Attempts before giving up on a monitor and using GDI for it.</summary>
    private const int FrameAttempts = 5;

    private readonly Dictionary<string, OutputDuplicator> _duplicators = new(StringComparer.Ordinal);
    private readonly GdiScreenCapture _fallback = new();
    private bool _disposed;

    public string Name => "Desktop Duplication";

    public bool IsAvailable { get; private set; } = true;

    /// <summary>Monitors served by GDI on the most recent capture, for diagnostics.</summary>
    public IReadOnlyList<string> FellBackTo { get; private set; } = [];

    public CapturedDesktop Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var desktop = MonitorEnumerator.Enumerate();
        var bounds = desktop.Bounds;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException($"virtual desktop has no area ({bounds.Width}x{bounds.Height})");
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        var fellBack = new List<string>();

        try
        {
            foreach (var monitor in desktop.Monitors)
            {
                if (TryCaptureMonitor(monitor, bitmap, bounds)) continue;

                fellBack.Add(monitor.DeviceName);
                CaptureMonitorWithGdi(monitor, bitmap, bounds);
            }

            FellBackTo = fellBack;
            return new CapturedDesktop(bitmap, desktop);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private bool TryCaptureMonitor(MonitorInfo monitor, Bitmap target, Rectangle bounds)
    {
        try
        {
            var duplicator = GetOrCreate(monitor);
            return duplicator is not null && duplicator.TryCopyInto(target, monitor, bounds);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // A lost duplication (mode change, fullscreen transition, driver reset) is normal
            // and recoverable — drop it so the next capture rebuilds it from scratch.
            Invalidate(monitor.DeviceName);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            Invalidate(monitor.DeviceName);
            return false;
        }
    }

    private OutputDuplicator? GetOrCreate(MonitorInfo monitor)
    {
        if (_duplicators.TryGetValue(monitor.DeviceName, out var existing)) return existing;

        var created = OutputDuplicator.Create(monitor.DeviceName);
        if (created is null) return null;

        _duplicators[monitor.DeviceName] = created;
        return created;
    }

    private void Invalidate(string deviceName)
    {
        if (!_duplicators.Remove(deviceName, out var duplicator)) return;
        duplicator.Dispose();
    }

    /// <summary>Blits one monitor with GDI, into its correct place in the composite.</summary>
    private static void CaptureMonitorWithGdi(MonitorInfo monitor, Bitmap target, Rectangle bounds)
    {
        using var graphics = Graphics.FromImage(target);
        graphics.CopyFromScreen(
            monitor.Bounds.X, monitor.Bounds.Y,
            monitor.Bounds.X - bounds.X, monitor.Bounds.Y - bounds.Y,
            new Size(monitor.Bounds.Width, monitor.Bounds.Height),
            CopyPixelOperation.SourceCopy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var duplicator in _duplicators.Values) duplicator.Dispose();
        _duplicators.Clear();
        _fallback.Dispose();
    }

    /// <summary>Holds the D3D device and duplication for a single monitor.</summary>
    private sealed class OutputDuplicator : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private ID3D11Texture2D? _staging;

        private OutputDuplicator(
            ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication)
        {
            _device = device;
            _context = context;
            _duplication = duplication;
        }

        /// <summary>
        /// Builds a duplicator for the monitor with the given device name, or null when the
        /// output can't be found or duplicated.
        /// </summary>
        public static OutputDuplicator? Create(string deviceName)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            for (uint adapterIndex = 0;
                 factory.EnumAdapters1(adapterIndex, out var adapter).Success;
                 adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0;
                         adapter.EnumOutputs(outputIndex, out var output).Success;
                         outputIndex++)
                    {
                        using (output)
                        {
                            if (!string.Equals(output.Description.DeviceName, deviceName, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            // A rotated display would need the frame transformed before use;
                            // GDI already handles rotation, so defer to it instead.
                            if (output.Description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            {
                                return null;
                            }

                            return CreateForOutput(adapter, output);
                        }
                    }
                }
            }

            return null;
        }

        private static OutputDuplicator? CreateForOutput(IDXGIAdapter1 adapter, IDXGIOutput output)
        {
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;

            try
            {
                // The device must live on the same adapter that drives this output, so
                // DriverType.Unknown is required alongside an explicit adapter.
                var result = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                    out device,
                    out context);

                if (result.Failure || device is null || context is null) return null;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                var duplication = output1.DuplicateOutput(device);

                var duplicator = new OutputDuplicator(device, context, duplication);
                device = null;                                  // ownership transferred
                context = null;
                return duplicator;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // DXGI_ERROR_UNSUPPORTED, or another process already holds duplication.
                return null;
            }
            finally
            {
                context?.Dispose();
                device?.Dispose();
            }
        }

        /// <summary>
        /// Acquires a frame and writes it into <paramref name="target"/> at the monitor's
        /// position. Returns false if no frame arrived within the retry budget.
        /// </summary>
        public bool TryCopyInto(Bitmap target, MonitorInfo monitor, Rectangle bounds)
        {
            for (var attempt = 0; attempt < FrameAttempts; attempt++)
            {
                var result = _duplication.AcquireNextFrame(
                    FrameTimeoutMs, out _, out var resource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // Nothing changed on screen. Normal for a static desktop; try again.
                    continue;
                }

                if (result.Failure || resource is null)
                {
                    result.CheckError();                        // throws for genuine failures
                    return false;
                }

                try
                {
                    using var frame = resource.QueryInterface<ID3D11Texture2D>();
                    CopyTextureInto(frame, target, monitor, bounds);
                    return true;
                }
                finally
                {
                    resource.Dispose();
                    _duplication.ReleaseFrame();
                }
            }

            return false;
        }

        private void CopyTextureInto(
            ID3D11Texture2D frame, Bitmap target, MonitorInfo monitor, Rectangle bounds)
        {
            var description = frame.Description;

            // The GPU texture can't be read directly; it has to go through a CPU-readable
            // staging copy. Cached, since its size only changes on a resolution change.
            if (_staging is null
                || _staging.Description.Width != description.Width
                || _staging.Description.Height != description.Height)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = description.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }

            _context.CopyResource(_staging, frame);

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var destination = new Rectangle(
                    monitor.Bounds.X - bounds.X,
                    monitor.Bounds.Y - bounds.Y,
                    Math.Min((int)description.Width, monitor.Bounds.Width),
                    Math.Min((int)description.Height, monitor.Bounds.Height));

                var data = target.LockBits(destination, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    // Row by row: the source and destination strides rarely match, and both
                    // are usually wider than the visible pixels.
                    var rowBytes = destination.Width * 4;
                    for (var y = 0; y < destination.Height; y++)
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy(
                                (byte*)mapped.DataPointer + (y * (long)mapped.RowPitch),
                                (byte*)data.Scan0 + (y * (long)data.Stride),
                                rowBytes,
                                rowBytes);
                        }
                    }
                }
                finally
                {
                    target.UnlockBits(data);
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            _staging?.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
