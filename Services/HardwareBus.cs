using Brilho.Models;
using Brilho.Native;

namespace Brilho.Services;

internal sealed class HardwareBus : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Link> _links = new();
    private readonly Dictionary<string, int> _pending = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Action<string> _onFailed;
    private volatile bool _stop;

    public HardwareBus(Action<string> onFailed)
    {
        _onFailed = onFailed;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Brilho DDC"
        };
        _thread.Start();
    }

    public void ReplaceLinks(IReadOnlyList<MonitorState> monitors)
    {
        var incoming = new Dictionary<string, Link>();
        foreach (var monitor in monitors)
        {
            if (!monitor.Hardware || monitor.Handle == 0)
                continue;
            incoming[monitor.Id] = new Link(monitor.Handle, monitor.MaxBrightness);
            monitor.Handle = 0;
        }

        List<nint> retired;
        lock (_gate)
        {
            retired = _links.Values.Select(link => link.Handle).ToList();
            _links.Clear();
            foreach (var pair in incoming)
                _links[pair.Key] = pair.Value;
            foreach (var id in _pending.Keys.Where(id => !_links.ContainsKey(id)).ToList())
                _pending.Remove(id);
        }

        foreach (var handle in retired)
            NativeMethods.DestroyPhysical(handle);
    }

    public void SetPercent(string id, int percent)
    {
        lock (_gate)
        {
            if (!_links.ContainsKey(id))
                return;
            _pending[id] = BrightnessMath.Clamp(percent);
        }

        _signal.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(1500);

        List<nint> handles;
        lock (_gate)
        {
            handles = _links.Values.Select(link => link.Handle).ToList();
            _links.Clear();
            _pending.Clear();
        }

        foreach (var handle in handles)
            NativeMethods.DestroyPhysical(handle);
        _signal.Dispose();
    }

    private void Loop()
    {
        while (!_stop)
        {
            _signal.WaitOne(400);
            if (_stop)
                break;

            List<(string Id, int Percent, Link Link)> batch;
            lock (_gate)
            {
                batch = new List<(string, int, Link)>();
                foreach (var pending in _pending)
                {
                    if (_links.TryGetValue(pending.Key, out var link))
                        batch.Add((pending.Key, pending.Value, link));
                }

                _pending.Clear();
            }

            foreach (var item in batch)
            {
                if (_stop)
                    break;

                var deviceValue = BrightnessMath.ToDevice(item.Percent, item.Link.Max);
                var ok = false;
                lock (_gate)
                {
                    if (_links.TryGetValue(item.Id, out var current) && current.Handle == item.Link.Handle)
                        ok = NativeMethods.SetVCPFeature(current.Handle, NativeMethods.BrightnessVcp, deviceValue);
                }

                if (!ok)
                {
                    Log.Info("Falha ao gravar brilho de " + item.Id);
                    _onFailed(item.Id);
                }

                Thread.Sleep(40);
            }
        }
    }

    private sealed record Link(nint Handle, uint Max);
}
