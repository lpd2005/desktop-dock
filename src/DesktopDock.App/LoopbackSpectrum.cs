using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopDock.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopDock.App;

// One serialized owner for COM and capture lifetime. Device callbacks only signal that owner;
// they never tear down audio objects under Windows' notification lock or on the UI thread.
internal sealed class LoopbackSpectrum : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly SpectrumAnalyzer analyzer = new();
    private Thread? worker;
    private volatile bool requested, disposed, running;
    private long lastSound;
    private int frames, starts;
    public event Action? Changed;
    public bool Running => running;
    internal int Frames => Volatile.Read(ref frames);
    internal int Starts => Volatile.Read(ref starts);

    public void SetActive(bool value, bool energySaver)
    {
        lock (gate) analyzer.AnalysisRate = energySaver ? 15 : 30;
        if (disposed || requested == value) return;
        requested = value;
        wake.Set();
        if (value && worker is null)
        {
            worker = new Thread(Pump) { IsBackground = true, Name = "Dock audio lifetime" };
            worker.SetApartmentState(ApartmentState.MTA); worker.Start();
        }
    }

    public double CopyTo(Span<float> destination, double sensitivity)
    {
        lock (gate)
        {
            analyzer.CopyTo(destination, sensitivity);
            return lastSound == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(lastSound).TotalSeconds;
        }
    }

    private void Pump()
    {
        try
        {
            while (!disposed)
            {
                if (!requested) { wake.WaitOne(); continue; }
                // Consume the command that started this session before waiting for its end.
                wake.WaitOne(0);
                bool failed = false;
                try
                {
                    using var devices = new MMDeviceEnumerator();
                    using var notifications = devices.CreateNotificationClient(useSynchronizationContext: false);
                    string? endpoint = null;
                    notifications.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) Signal(); };
                    notifications.DeviceStateChanged += (_, e) => { if (e.DeviceId == endpoint) Signal(); };
                    using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    endpoint = device.ID;
                    using var capture = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture()
                        .WithEventSync().WithBufferLength(40).WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).Build();
                    capture.DataAvailable += OnData;
                    capture.RecordingStopped += OnStopped;
                    try
                    {
                        if (!requested || disposed) continue;
                        capture.StartRecording(); Interlocked.Increment(ref starts);
                        running = true; Changed?.Invoke();
                        wake.WaitOne();
                    }
                    finally { capture.DataAvailable -= OnData; capture.RecordingStopped -= OnStopped; }
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException or ArgumentException
                    or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                { failed = true; }
                finally
                {
                    running = false;
                    lock (gate) lastSound = 0;
                    Changed?.Invoke();
                }
                // A missing device or audio service may recover without a fresh endpoint callback.
                // Back off on errors only; a running or disabled capture has no polling loop here.
                if (failed && requested && !disposed) wake.WaitOne(5000);
            }
        }
        finally { wake.Dispose(); }
    }

    private void OnStopped(object? sender, StoppedEventArgs args) => Signal();
    private void Signal() { if (!disposed) wake.Set(); }
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!requested || disposed) return;
        bool audible;
        lock (gate)
        {
            if (!analyzer.Append(MemoryMarshal.Cast<byte, float>(buffer))) return;
            audible = analyzer.Audible;
            if (audible) lastSound = Stopwatch.GetTimestamp();
        }
        Interlocked.Increment(ref frames);
        if (audible) Changed?.Invoke();
    }

    internal void ReconnectForChecks() => Signal();
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; requested = false;
        if (worker is null) wake.Dispose();
        else { wake.Set(); worker.Join(2000); }
    }
}
