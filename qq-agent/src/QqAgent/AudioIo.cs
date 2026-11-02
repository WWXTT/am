using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace QqAgent;

/// <summary>语音条通路的音频侧（2026-10-25）：QQNT 的「语音消息」按钮只认系统录音设备——
/// 机器无实体麦克风时按钮点了没反应。本机 Realtek「立体声混音」= 模拟输出回环，启用并设为
/// 默认录音设备后，QQ 录到的 = Realtek 扬声器里播放的内容；播放侧用 NAudio WasapiOut 定向到
/// Realtek 渲染端点（不依赖也不修改系统默认播放设备），即可把本地音频文件变成真语音条。
/// 系统提示音等一切走 Realtek 的声音都会混入——这是免驱动方案的固有代价（干净隔离需 VB-CABLE）。
/// COM 互操作教训（2026-10-25）：设备枚举/端点音量/播放一律走 NAudio（自家 ComImport 类与
/// NAudio 的同 CLSID 包装撞 RCW 缓存；winmm waveOut 本机全格式被拒；手写 IAudioClient 的
/// Initialize 在 .NET10 调用桩抛 ArgumentException）——自写 interop 只留 NAudio 没有的 IPolicyConfig。</summary>
internal static class AudioIo
{
    // ---- IPolicyConfig（未公开但业界标准，SoundSwitch 同款）：启用/禁用端点 + 设默认端点 ----
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientCom { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat(string id, IntPtr format);
        int GetDeviceFormat(string id, int bDefault, IntPtr format);
        int ResetDeviceFormat(string id);
        int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod(string id, int bDefault, out long period, out long basePeriod);
        int SetProcessingPeriod(string id, ref long period);
        int GetShareMode(string id, IntPtr mode);
        int SetShareMode(string id, IntPtr mode);
        int GetPropertyValue(string id, int bFxStore, ref PropertyKey key, out PropVariant value);
        int SetPropertyValue(string id, int bFxStore, ref PropertyKey key, ref PropVariant value);
        int SetDefaultEndpoint(string id, int role);
        int SetDeviceState(string id, int state);   // 1=Active 2=Disabled
        int SetDeviceVisibility(string id, int visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Fmtid; public int Pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr P;
    }

    private const int StateActive = 1, StateDisabled = 2;
    private const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;

    internal sealed record EndpointInfo(string Id, string Name, int State, bool IsDefault);

    /// <summary>列出某方向（0=播放 1=录音）全部端点，标注当前默认。</summary>
    internal static List<EndpointInfo> List(int flow)
    {
        var df = flow == 1 ? DataFlow.Capture : DataFlow.Render;
        using var en = new MMDeviceEnumerator();
        string defId = "";
        try { defId = en.GetDefaultAudioEndpoint(df, (Role)RoleConsole).ID; } catch { }
        var list = new List<EndpointInfo>();
        foreach (var d in en.EnumerateAudioEndPoints(df, DeviceState.All))
        {
            using (d)
                list.Add(new EndpointInfo(d.ID, d.FriendlyName, (int)d.State, d.ID == defId));
        }
        return list;
    }

    /// <summary>启用名为 nameContains 的录音端点（默认「立体声混音」），设为三类角色默认，
    /// 并把端点录音音量拉满、去静音。幂等。返回人类可读的执行摘要。</summary>
    internal static string EnsureCaptureReady(string nameContains)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice Find()
        {
            var hit = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All)
                .FirstOrDefault(d => d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                var all = string.Join(" / ", List(1).ConvertAll(e => e.Name));
                throw new InvalidOperationException($"找不到名字含「{nameContains}」的录音端点（现有: {all}）");
            }
            return hit;
        }

        using (var hit = Find())
        {
            var pc = (IPolicyConfig)new PolicyConfigClientCom();
            var did = new List<string>();
            if (!hit.State.HasFlag(DeviceState.Active))
            {
                Marshal.ThrowExceptionForHR(pc.SetDeviceState(hit.ID, StateActive));
                did.Add("已启用");
            }
            string defId;
            try { defId = en.GetDefaultAudioEndpoint(DataFlow.Capture, (Role)RoleConsole).ID; }
            catch { defId = ""; }
            if (defId != hit.ID)
            {
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(hit.ID, RoleConsole));
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(hit.ID, RoleMultimedia));
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(hit.ID, RoleCommunications));
                did.Add("已设默认(console/multimedia/communications)");
            }
            did.Add("录音电平=1.0 且非静音");
        }
        // 音量用新实例调（刚启用的旧句柄可能失效）
        using (var fresh = Find())
        {
            fresh.AudioEndpointVolume.Mute = false;
            fresh.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
        }
        return $"「{nameContains}」" + string.Join("，", new[] { "录音端点就绪" }) + "（电平已拉满）";
    }

    /// <summary>回滚：禁用该录音端点（恢复机器原状——原本就没有可用麦克风/默认为空）。</summary>
    internal static string DisableCapture(string nameContains)
    {
        using var en = new MMDeviceEnumerator();
        var hit = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All)
            .FirstOrDefault(d => d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"找不到名字含「{nameContains}」的录音端点");
        using (hit)
        {
            var pc = (IPolicyConfig)new PolicyConfigClientCom();
            Marshal.ThrowExceptionForHR(pc.SetDeviceState(hit.ID, StateDisabled));
            return $"「{hit.FriendlyName}」已禁用（默认设备随 Windows 自动回退）";
        }
    }

    // ---- 播放侧：NAudio WasapiOut 定向渲染 ----

    /// <summary>ffmpeg 把任意输入音频归一化成 48k/16bit/立体声 wav（音量倍率内嵌进文件）。
    /// 返回临时 wav 路径（调用方负责删）。</summary>
    internal static string NormalizeToWav(string src, double volume)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"qqagent-voice-{Guid.NewGuid():N}.wav");
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(src);
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add($"volume={volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-sample_fmt");
        psi.ArgumentList.Add("s16");
        psi.ArgumentList.Add(tmp);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败（PATH 里没有？）");
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000) || p.ExitCode != 0 || !File.Exists(tmp))
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException($"ffmpeg 转码失败(exit={p.ExitCode}): {err[^Math.Min(300, err.Length)..]}");
        }
        return tmp;
    }

    /// <summary>标准 PCM wav 的时长（秒）；非 wav/解析失败返回 -1。</summary>
    internal static double WavDuration(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != 0x46464952) return -1;   // "RIFF"
            br.ReadInt32();
            if (br.ReadUInt32() != 0x45564157) return -1;   // "WAVE"
            int channels = 0, sampleRate = 0, bits = 0;
            long dataLen = 0;
            while (fs.Position < fs.Length - 8)
            {
                var id = br.ReadUInt32();
                var len = br.ReadInt32();
                if (id == 0x20746D66)                      // "fmt "
                {
                    br.ReadInt16();                        // format
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); br.ReadInt16();
                    bits = br.ReadInt16();
                    if (len > 16) fs.Seek(len - 16, SeekOrigin.Current);
                }
                else if (id == 0x61746164)                 // "data"
                {
                    dataLen = len;
                    fs.Seek(len + (len & 1), SeekOrigin.Current);
                }
                else fs.Seek(len + (len & 1), SeekOrigin.Current);
            }
            if (channels <= 0 || sampleRate <= 0 || bits <= 0 || dataLen <= 0) return -1;
            return dataLen / (double)(sampleRate * channels * (bits / 8));
        }
        catch { return -1; }
    }

    /// <summary>同步播放 PCM wav 到名字含 renderNameContains 的渲染端点（匹配不到用系统默认）。
    /// 共享模式；阻塞至播完（录音时长=播放时长）。capSeconds>0 时只播前 N 秒。</summary>
    internal static void PlayWavBlocking(string wav, string renderNameContains, double capSeconds)
    {
        var (data, fmt) = ReadPcmWav(wav);
        if (capSeconds > 0 && fmt.AvgBytesPerSec > 0)
        {
            var cut = (int)Math.Min(data.LongLength, (long)(capSeconds * fmt.AvgBytesPerSec));
            cut -= cut % fmt.BlockAlign;   // 对齐帧边界
            if (cut < data.Length) Array.Resize(ref data, cut);
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => !string.IsNullOrEmpty(renderNameContains) &&
                    d.FriendlyName.Contains(renderNameContains, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, (Role)RoleConsole);
        Console.WriteLine($"[voice-play] endpoint='{device.FriendlyName}' fmt={fmt.SamplesPerSec}Hz/{fmt.Channels}ch/{fmt.BitsPerSample}bit bytes={data.Length}");

        using var waveOut = new NAudio.Wave.WasapiOut(device,
            AudioClientShareMode.Shared, false, 200);
        using var source = new NAudio.Wave.RawSourceWaveStream(
            new MemoryStream(data), new NAudio.Wave.WaveFormat(fmt.SamplesPerSec, fmt.BitsPerSample, fmt.Channels));
        waveOut.Init(source);
        waveOut.Play();
        var sw = Stopwatch.StartNew();
        while (waveOut.PlaybackState == NAudio.Wave.PlaybackState.Playing)
        {
            if (sw.ElapsedMilliseconds > (capSeconds > 0 ? capSeconds : 60) * 1000 + 15_000)
                throw new InvalidOperationException("WASAPI 播放超时");
            Thread.Sleep(100);
        }
        Console.WriteLine($"[voice-play] done {sw.Elapsed.TotalSeconds:F1}s");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public short FormatTag, Channels, BitsPerSample;
        public int SamplesPerSec, AvgBytesPerSec;
        public short BlockAlign, cbSize;
    }

    private static (byte[] Data, WAVEFORMATEX Fmt) ReadPcmWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        if (br.ReadUInt32() != 0x46464952) throw new InvalidOperationException("不是 wav: " + path);
        br.ReadInt32();
        if (br.ReadUInt32() != 0x45564157) throw new InvalidOperationException("不是 wav: " + path);
        while (ms.Position < ms.Length - 8)
        {
            var id = br.ReadUInt32();
            var len = br.ReadInt32();
            if (id == 0x20746D66)
            {
                var fmtTag = br.ReadInt16();
                var f = new WAVEFORMATEX
                {
                    FormatTag = fmtTag,
                    Channels = br.ReadInt16(),
                    SamplesPerSec = br.ReadInt32(),
                    AvgBytesPerSec = br.ReadInt32(),
                    BlockAlign = br.ReadInt16(),
                    BitsPerSample = br.ReadInt16(),
                    cbSize = 0,
                };
                if (len > 16) ms.Seek(len - 16, SeekOrigin.Current);
                if (fmtTag != 1) throw new InvalidOperationException("wav 非 PCM（应先经 ffmpeg 归一化）");
                // 找 data 块
                while (ms.Position < ms.Length - 8)
                {
                    var id2 = br.ReadUInt32();
                    var len2 = br.ReadInt32();
                    if (id2 == 0x61746164)
                    {
                        var data = br.ReadBytes(len2);
                        return (data, f);
                    }
                    ms.Seek(len2 + (len2 & 1), SeekOrigin.Current);
                }
                throw new InvalidOperationException("wav 无 data 块");
            }
            ms.Seek(len + (len & 1), SeekOrigin.Current);
        }
        throw new InvalidOperationException("wav 无 fmt 块");
    }
}
