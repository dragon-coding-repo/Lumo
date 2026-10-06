using Lumo.Core;
using Xunit;

namespace Lumo.Core.Tests;

/// <summary>
/// 「下回不再提示」必须真的存得住。
/// </summary>
/// <remarks>
/// 这个标志一旦存不住，用户就会看到每次启动都弹同一个窗，而且完全无从排查
/// —— 因为保存失败被静默吞掉了。这里把往返行为钉死。
/// </remarks>
public class FirstRunFlagTests
{
    private static ConfigStore StoreFor(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"lumo-firstrun-{tag}-{Environment.ProcessId}");
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return new ConfigStore(Path.Combine(d, "config.json"));
    }

    [Fact]
    public void FreshInstallShowsHelp()
    {
        Assert.True(StoreFor("fresh").Load().ShowFirstRunHelp);
    }

    [Fact]
    public void FlagSurvivesSaveAndReload()
    {
        var store = StoreFor("roundtrip");
        var cfg = store.Load();
        cfg.ShowFirstRunHelp = false;      // 用户勾了「下回不再提示」
        store.Save(cfg);

        Assert.False(store.Load().ShowFirstRunHelp);
    }

    [Fact]
    public void FlagIsWrittenIntoConfigFile()
    {
        var store = StoreFor("file");
        var cfg = store.Load();
        cfg.ShowFirstRunHelp = false;
        store.Save(cfg);

        // 直接查文件，确认字段真的落盘了（而不是靠默认值凑巧对上）
        var json = File.ReadAllText(store.Path);
        Assert.Contains("showFirstRunHelp", json);
        Assert.Contains("false", json);
    }

    [Fact]
    public void UncheckedFlagStaysTrue()
    {
        // 不勾选就保持 true —— 语义完全交给用户，程序不擅自改状态
        var store = StoreFor("unchecked");
        var cfg = store.Load();
        Assert.True(cfg.ShowFirstRunHelp);
    }

    [Fact]
    public void OlderConfigWithoutFieldDefaultsToShowing()
    {
        // 老版本配置没有这个字段，应该按「首次运行」处理并弹一次
        var store = StoreFor("old");
        File.WriteAllText(store.Path, """{"version":2,"targetLang":"zh"}""");
        Assert.True(store.Load().ShowFirstRunHelp);
    }

    [Fact]
    public void ExplicitFalseInFileIsHonoured()
    {
        var store = StoreFor("explicit");
        File.WriteAllText(store.Path, """{"version":2,"showFirstRunHelp":false}""");
        Assert.False(store.Load().ShowFirstRunHelp);
    }
}
