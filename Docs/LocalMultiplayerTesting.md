# Local Multiplayer Testing

## Purpose

Local host/client testing is useful for quick iteration, but it is not a faithful network performance test. When the host and multiple clients run on one PC, all instances compete for the same CPU, GPU, memory bandwidth, and OS scheduling time. Fusion's displayed RTT can rise when a local process stalls, even if the network path itself is fine.

## Runtime Safeguard

`MenuConnection.CreateRunner()` sets:

```csharp
Application.runInBackground = true;
QualitySettings.vSyncCount = 0;
Application.targetFrameRate = 30;
```

This is required for local multi-window testing. Without background processing, Unity can throttle unfocused player windows. A throttled host or client may miss simulation/network updates, which appears as high ping in Fusion/Photon stats. The 30 FPS cap keeps local test clients responsive without letting uncapped rendering starve the host and other clients on the same PC.

## Recommended Local Setup

- Prefer one host plus one client on the same machine for quick functionality checks.
- For three or more players, use a separate machine for at least the host, or run a dedicated server build.
- Keep local client graphics cheap: the connection flow caps runtime framerate to 30 and disables VSync; lower quality settings if local instances still compete for CPU/GPU time.
- Watch CPU/GPU frametime together with Fusion RTT. If disconnecting one local instance immediately fixes ping, the bottleneck is usually local scheduling/load rather than dirty network state.
- Treat cloud/remote-machine tests as the source of truth for real network latency.

## What This Does Not Change

The background-processing safeguard does not reduce per-character simulation cost and does not change Fusion replication. If high RTT also happens with separate machines, investigate networked state dirtiness, object counts, and per-tick state changes.
