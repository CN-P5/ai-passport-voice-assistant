using System.Text;
using AiPassportVoice.Core.Adpcm;
using AiPassportVoice.Core.Audio;
using AiPassportVoice.Core.Config;
using AiPassportVoice.Core.Forwarding;
using AiPassportVoice.Core.Input;

var failures = 0;
var total = 0;

void Check(string name, bool ok)
{
    total++;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
}

static bool Throws(Action action)
{
    try { action(); return false; }
    catch { return true; }
}

static byte[] Evt(string name, string extra = "")
{
    var body = $"{{\"event\":\"{name}\"{(extra.Length > 0 ? "," + extra : "")}}}\n";
    return Encoding.UTF8.GetBytes(body);
}

Console.WriteLine("== KeySpec ==");
var single = KeySpec.Resolve("right-shift");
Check("单键 right-shift 解析", single.Count == 1 && single[0].Vk == 0xA1 && single[0].Scan == 0x36);

var combo = KeySpec.Resolve("win+h");
Check("组合键 win+h", combo.Count == 2 && combo[0].Name == "left-win" && combo[1].Name == "h");

var triple = KeySpec.Resolve("ctrl+shift+space");
Check("组合键 ctrl+shift+space", triple.Count == 3 && triple[2].Vk == 0x20);

Check("未知键名抛异常", Throws(() => KeySpec.Resolve("nope")));
Check("修饰键位置非法抛异常", Throws(() => KeySpec.Resolve("h+ctrl")));

Console.WriteLine("== ADPCM ==");
var block = new byte[AdpcmDecoder.BlockBytes];
block[0] = 0; block[1] = 0; block[2] = 0;
var samples = AdpcmDecoder.DecodeBlock(block);
Check("解码长度 1600", samples.Length == AdpcmDecoder.BlockSamples);
Check("首样本等于 predictor", samples[0] == 0);
Check("静音块输出恒定 0", samples.All(s => s == 0));

var pcm = AdpcmDecoder.DecodeToPcmBytes(block);
Check("PCM 字节数 3200", pcm.Length == 3200);
Check("短块返回空", AdpcmDecoder.DecodeBlock(new byte[10]).Length == 0);

Console.WriteLine("== Forwarder ==");
var inj = new FakeInjector();
var enter = new FakeInjector();
var sink = new FakeSink();
var fwd = new Forwarder(inj, enter, sink, _ => { });

fwd.OnEvent(Evt("voice.start"));
Check("voice.start 注入按下并清空缓冲", fwd.Active && inj.Starts == 1 && sink.Clears == 1);

var audio = new byte[806];
audio[0] = 0;
audio[1] = 0x80;
fwd.OnAudio(audio);
fwd.OnAudio(audio);
Check("音频 seq 去重", fwd.AudioBlocks == 1 && sink.Writes == 1);

fwd.OnEvent(Evt("voice.end"));
Check("voice.end 抬起并 drain", !fwd.Active && inj.Stops == 1 && sink.Drains == 1);

var inj2 = new FakeInjector();
var enter2 = new FakeInjector();
var fwd2 = new Forwarder(inj2, enter2, new FakeSink(), _ => { });
fwd2.OnEvent(Evt("key.action", "\"action\":\"enter\""));
fwd2.OnEvent(Evt("key.action", "\"action\":\"enter\""));
Check("下键 0.35s 去抖只点一次", enter2.Clicks == 1);

var inj3 = new FakeInjector();
var fwd3 = new Forwarder(inj3, new FakeInjector(), new FakeSink{ Enabled = true }, _ => { });
fwd3.OnEvent(Evt("voice.start"));
Thread.Sleep(2600);
fwd3.CheckHealth();
Check("静音超时自动抬起热键", !fwd3.Active && inj3.Stops == 1);

var fwd4 = new Forwarder(new FakeInjector(), new FakeInjector(), new FakeSink{ Enabled = false }, _ => { });
fwd4.OnEvent(Evt("voice.start"));
Check("声卡未就绪拒绝开始", !fwd4.Active);

fwd4.OnEvent(Encoding.UTF8.GetBytes("garbage\n"));
Check("畸形事件被忽略", !fwd4.Active);

Console.WriteLine("== AppConfig ==");
var tmp = Path.Combine(AppContext.BaseDirectory, "apv-config-test.json");
var cfg = new AppConfig { HotkeyKey = "f9", UdpPort = 33334, HotkeyMode = "tap" };
cfg.Save(tmp);
var loaded = AppConfig.Load(tmp);
Check("配置保存/读取往返", loaded.HotkeyKey == "f9" && loaded.UdpPort == 33334 && loaded.HotkeyMode == "tap");
try { File.Delete(tmp); } catch { }

Console.WriteLine();
Console.WriteLine("== 音频输出设备（验证全名是否被 API 截断） ==");
foreach (var d in AiPassportVoice.Interop.Audio.AudioDeviceProbe.EnumerateOutputs())
    Console.WriteLine($"  OUT · ({d.Length}) {d}");
foreach (var d in AiPassportVoice.Interop.Audio.AudioDeviceProbe.EnumerateInputs())
    Console.WriteLine($"  IN  · ({d.Length}) {d}");

Console.WriteLine();
Console.WriteLine($"共 {total} 项，失败 {failures} 项");
return failures == 0 ? 0 : 1;

sealed class FakeInjector : IKeyInjector
{
    public int Starts, Stops, Clicks;
    public bool IsActive { get; private set; }
    public void Start() { Starts++; IsActive = true; }
    public void Stop() { Stops++; IsActive = false; }
    public void Click() => Clicks++;
}

sealed class FakeSink : IAudioSink
{
    public int Clears, Drains, Writes;
    public bool Enabled { get; set; } = true;
    public string? DeviceName => "fake";
    public void WriteBlock(ReadOnlySpan<byte> adpcmBlock) => Writes++;
    public void Clear() => Clears++;
    public void Drain(TimeSpan timeout) => Drains++;
}
