# DeskPet Windows 移植 Phase 0 — 工程底座 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> 技术栈裁决（2026-08-19）：WinUI 3 → WPF（本机无管理员，VS Build Tools 不可安装）；本计划按裁决更新为 WPF。

**Goal:** 建立 `DeskPet.Windows/` WPF 解决方案并跑通最小闭环：应用启动 → 无边框透明置顶窗口 → 加载内置 BoringPet 素材 → 逐帧播放 idle 动画。Core 层（可单测的纯逻辑）骨架与帧管线就位，为 Phase 1（宠物核心逻辑）打底。

**Architecture:** 三层分层 — `DeskPet.Core`（net8.0 纯类库，零 UI 框架依赖，xUnit 单测）、`DeskPet.Windows`（WPF 应用：Services 平台实现 + ViewModels + Views）、`tests/`（xUnit 测试项目）。帧引擎按 Swift `SpriteAnimator.swift` 移植：`IImageDecoder` 注入 + `PetSkin` 帧缓存（部分帧预缓存 / 流式补齐 / loopPoint 无缝循环 / 回窝节能）。

**Tech Stack:** .NET 8 LTS、WPF（.NET 内置，纯 dotnet CLI 构建，无需 Visual Studio / VS Build Tools）、xUnit、CommunityToolkit.Mvvm、System.Text.Json；解码用 WPF `System.Windows.Media.Imaging.BitmapDecoder`。

**Spec:** `docs/superpowers/specs/2026-08-19-windows-port-selection.md`

## Global Constraints

- .NET 8 LTS；WPF（`UseWPF=true`）应用；纯 dotnet CLI 构建（本机无管理员、VS Build Tools 不可安装）；框架依赖输出即可拷贝运行，无需 WindowsAppSDK 的免安装打包属性。
- `DeskPet.Core` 只依赖 BCL + `System.Text.Json`，禁止引用 WPF/WinForms（保证单测可跑）。
- 项目/命名/文件夹与 Swift 源码保持同名对应（`PetSkin`/`PetManager`/`PetAction`…），便于逐行对照移植。
- 内置素材：从 macOS 版 `deskpet/BoringPet/` 拷贝到 `DeskPet.Windows/assets/BoringPet/`（本分支 macOS 源码保留原样）。
- 工程目录均在 `DeskPet.Windows/` 下，与 macOS 源码隔离。
- 许可：继承 GPLv3。

> WPF 工程布局与 WinUI 3 不同：无 `WindowsPackageType`/`WindowsAppSDKSelfContained` 属性、无 `Microsoft.WindowsAppSDK`/`Microsoft.Windows.SDK.BuildTools` 包引用，csproj 用 `UseWPF=true`（+ 托盘所需的 `UseWindowsForms=true`）；`app.manifest` 非必需，仅本项目保留它声明 `PerMonitorV2` DPI。App 为 `System.Windows.Application`，窗口为 WPF `Window`。

---

### Task 1: 解决方案脚手架（WPF 工程）

**Files:**
- Create: `DeskPet.Windows/DeskPet.Windows.sln`
- Create: `DeskPet.Windows/src/DeskPet.Windows/DeskPet.Windows.csproj`
- Create: `DeskPet.Windows/src/DeskPet.Windows/App.xaml` + `App.xaml.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/MainWindow.xaml` + `MainWindow.xaml.cs`
- Create: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/DeskPet.Windows.Core.Tests.csproj`
- Create: `DeskPet.Windows/src/DeskPet.Core/DeskPet.Core.csproj`（空类库，Task 2 填代码）

**Interfaces:**
- Consumes: 无（本任务为纯脚手架）。
- Produces: 解决方案结构；`DeskPet.Core` 类库项目（后续所有 Core 逻辑的宿主）。

- [ ] **Step 1: 拷贝素材目录**

```bash
Copy-Item -Recurse "deskpet/BoringPet" "DeskPet.Windows/assets/BoringPet"
```

- [ ] **Step 2: 创建 Core 类库**

`DeskPet.Windows/src/DeskPet.Core/DeskPet.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: 创建 WPF 应用项目**

`DeskPet.Windows/src/DeskPet.Windows/DeskPet.Windows.csproj`（关键属性：`UseWPF=true`、`UseWindowsForms=true`（托盘用）、无 WindowsAppSDK 包）:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RootNamespace>DeskPet.Windows</RootNamespace>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <ProjectReference Include="..\DeskPet.Core\DeskPet.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="..\..\assets\BoringPet\**" Link="assets/BoringPet/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="All" />
  </ItemGroup>
</Project>
```

`App.xaml.cs` 里用 `System.Windows.Application`（`ShutdownMode="OnExplicitShutdown"`）+ 创建 `MainWindow`（初始显示一个 `TextBlock`，Task 7 换成宠物窗口）。`app.manifest` 可选，本项目保留它声明 `dpiAwareness=PerMonitorV2`。

- [ ] **Step 4: 创建测试项目**

`DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/DeskPet.Windows.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <ProjectReference Include="..\..\src\DeskPet.Core\DeskPet.Core.csproj" />
    <ProjectReference Include="..\..\src\DeskPet.Windows\DeskPet.Windows.csproj" />
  </ItemGroup>
</Project>
```
加一个冒烟测试 `SmokeTests.cs`（`1+1==2`）。

- [ ] **Step 5: 生成 sln 并加入三个项目**

```bash
dotnet new sln -n DeskPet.Windows -o DeskPet.Windows
dotnet sln "DeskPet.Windows/DeskPet.Windows.sln" add "DeskPet.Windows/src/DeskPet.Core/DeskPet.Core.csproj" "DeskPet.Windows/src/DeskPet.Windows/DeskPet.Windows.csproj" "DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/DeskPet.Windows.Core.Tests.csproj"
```

- [ ] **Step 6: 构建并运行验证**

Run: `dotnet build "DeskPet.Windows/DeskPet.Windows.sln" -c Debug`
Run: `dotnet test "DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/DeskPet.Windows.Core.Tests.csproj"`
Run: 启动 `src/DeskPet.Windows`（`dotnet run` 或直接运行 exe），确认弹出窗口。
Expected: 构建成功、冒烟测试 PASS、应用启动显示窗口。

- [ ] **Step 7: Commit**

```bash
git add DeskPet.Windows docs
git commit -m "feat(windows): scaffold WPF solution with Core/test projects"
```

---

### Task 2: Core 枚举 — PetAction / PetLifeState

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/PetAction.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetActionTests.cs`

**Interfaces:**
- Consumes: 无。
- Produces: `PetAction`（13 值：Idle,Walk,Sleep,Eat,Happy,Hurt,Drag,Music,Climb,Hang,Fall,Dizzy,Yawn,Home）+ 扩展方法 `FolderName()` / `DefaultFps()` / `LoopsByDefault()`；`PetLifeState`（Roaming,AtHome）。供 Task 3/4/6 使用。

- [ ] **Step 1: 写失败测试**

`PetActionTests.cs`:
```csharp
public class PetActionTests
{
    [Theory]
    [InlineData(PetAction.Idle, "idle", 6, true)]
    [InlineData(PetAction.Walk, "walk", 12, true)]
    [InlineData(PetAction.Music, "music", 14, true)]
    [InlineData(PetAction.Fall, "fall", 20, false)]
    [InlineData(PetAction.Yawn, "yawn", 8, false)]
    [InlineData(PetAction.Eat, "eat", 10, false)]
    public void Action_Metadata_Matches_Swift(PetAction a, string folder, double fps, bool loops)
    {
        Assert.Equal(folder, a.FolderName());
        Assert.Equal(fps, a.DefaultFps());
        Assert.Equal(loops, a.LoopsByDefault());
    }
    [Fact]
    public void All_Enum_Values_Have_Unique_FolderNames()
        => Assert.Equal(PetActionExtensions.All().Count, PetActionExtensions.All().Select(a => a.FolderName()).Distinct().Count());
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test .../DeskPet.Windows.Core.Tests.csproj --filter "PetActionTests"` — Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现枚举**

`PetAction.cs`:
```csharp
namespace DeskPet.Core.Pet;
public enum PetAction { Idle, Walk, Sleep, Eat, Happy, Hurt, Drag, Music, Climb, Hang, Fall, Dizzy, Yawn, Home }
public enum PetLifeState { Roaming, AtHome }

public static class PetActionExtensions
{
    public static string FolderName(this PetAction a) => a.ToString().ToLowerInvariant();
    public static double DefaultFps(this PetAction a) => a switch
    {
        PetAction.Walk => 12, PetAction.Music or PetAction.Drag => 14,
        PetAction.Climb => 10, PetAction.Hang => 5, PetAction.Fall => 20,
        PetAction.Dizzy or PetAction.Yawn => 8, PetAction.Idle or PetAction.Sleep => 6,
        _ => 10 // Eat, Happy, Hurt, Home
    };
    public static bool LoopsByDefault(this PetAction a) => a switch
    {
        PetAction.Eat or PetAction.Fall or PetAction.Happy or PetAction.Hurt or PetAction.Yawn => false,
        _ => true
    };
    public static IReadOnlyList<PetAction> All() => Enum.GetValues<PetAction>();
}
```

- [ ] **Step 4: 运行确认通过**

Run: 同上 filter。Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add DeskPet.Windows/src/DeskPet.Core DeskPet.Windows/tests/DeskPet.Windows.Core.Tests
git commit -m "feat(core): port PetAction/PetLifeState enums with metadata"
```

---

### Task 3: Core 配置解析 — PetSkinConfig / PetActionConfig

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/PetSkinConfig.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetSkinConfigTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `PetAction`。
- Produces: `PetSkinConfig { double? Fps; double? Scale; Dictionary<string,PetActionConfig>? Actions }`、`PetActionConfig { double? Fps; int? Loops }`，含 `static PetSkinConfig Parse(string json)`（容错：解析失败返回默认）。Task 4 加载素材目录时调用。

- [ ] **Step 1: 写失败测试**

```csharp
public class PetSkinConfigTests
{
    [Fact]
    public void Parse_Full_Config()
    {
        var json = """{"fps":8,"scale":1.2,"actions":{"idle":{"fps":6},"walk":{"fps":12,"loops":1}}}""";
        var c = PetSkinConfig.Parse(json);
        Assert.Equal(8, c.Fps); Assert.Equal(1.2, c.Scale);
        Assert.Equal(6, c.Actions!["idle"]!.Fps);
        Assert.Equal(1, c.Actions!["walk"]!.Loops);
    }
    [Fact]
    public void Parse_Empty_Json_Returns_Defaults()
        => Assert.Null(PetSkinConfig.Parse("").Fps);
    [Fact]
    public void Parse_Invalid_Json_Does_Not_Throw()
        => Assert.NotNull(PetSkinConfig.Parse("{oops"));
}
```

- [ ] **Step 2: 运行确认失败**（类型不存在 → 编译失败）

- [ ] **Step 3: 实现**

```csharp
namespace DeskPet.Core.Pet;
public sealed record PetActionConfig { public double? Fps { get; init; } public int? Loops { get; init; } }
public sealed record PetSkinConfig
{
    public double? Fps { get; init; }
    public double? Scale { get; init; }
    public Dictionary<string, PetActionConfig>? Actions { get; init; }
    public static PetSkinConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PetSkinConfig();
        try { return JsonSerializer.Deserialize<PetSkinConfig>(json) ?? new PetSkinConfig(); }
        catch (JsonException) { return new PetSkinConfig(); }
    }
}
```
`DeskPet.Core.csproj` 增加 `<PackageReference Include="System.Text.Json" Version="8.*" />`（net8 也可直接用 BCL 内置，可不加包）。

- [ ] **Step 4: 运行确认通过** — Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): port PetSkinConfig JSON parsing"
```

---

### Task 4: Core 素材目录扫描 — 变体解析 + 帧/音效收集

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/SkinDirectoryScanner.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/SkinDirectoryScannerTests.cs`

**Interfaces:**
- Consumes: `PetAction`（Task 2）。
- Produces: `record SkinScanResult { Dictionary<PetAction, List<List<string>>> VariantUrls; Dictionary<PetAction,string> Sounds; int FrameCount; }`；`static SkinScanResult Scan(string rootDir)`。归并规则：`<action>_<n>` 归并到 `<action>` 变体（按 n 升序）；无变体单目录（`idle`）作为单变体；PNG/JPG 按文件名自然排序；音效取 `sound.m4a|mp3|wav|aiff` 第一个；没有有效目录返回空。Task 6 的 `PetSkin` 消费此结果。

- [ ] **Step 1: 写失败测试（用临时目录造素材）**

```csharp
public class SkinDirectoryScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "skin_" + Guid.NewGuid().ToString("N"));
    private void Mk(string rel) { var p = Path.Combine(_dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }

    [Fact]
    public void Scan_Resolves_Variants_And_Natural_Sort()
    {
        Mk("idle_0/1.png"); Mk("idle_0/2.png"); Mk("idle_0/10.png");
        Mk("idle_1/1.png");
        Mk("walk_0/1.png"); Mk("walk_0/2.png");
        var r = SkinDirectoryScanner.Scan(_dir);
        Assert.NotNull(r.VariantUrls[PetAction.Idle]);
        Assert.Equal(2, r.VariantUrls[PetAction.Idle].Count);
        Assert.Equal(new[]{"1.png","2.png","10.png"}, r.VariantUrls[PetAction.Idle][0].Select(p=>Path.GetFileName(p)));
        Assert.Equal(new[]{"1.png","2.png"}, r.VariantUrls[PetAction.Walk][0].Select(p=>Path.GetFileName(p)));
    }
    [Fact]
    public void Scan_Picks_Up_Sound()
    {
        Mk("walk_0/1.png"); Mk("walk_0/sound.m4a");
        var r = SkinDirectoryScanner.Scan(_dir);
        Assert.True(r.Sounds[PetAction.Walk].EndsWith("sound.m4a"));
    }
    [Fact]
    public void Scan_Empty_Dir_Returns_Empty() => Assert.Empty(SkinDirectoryScanner.Scan(_dir).VariantUrls);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

```csharp
namespace DeskPet.Core.Pet;
public sealed record SkinScanResult
{
    public Dictionary<PetAction, List<List<string>>> VariantUrls { get; init; } = new();
    public Dictionary<PetAction, string> Sounds { get; init; } = new();
    public int FrameCount { get; init; }
}

public static class SkinDirectoryScanner
{
    public static SkinScanResult Scan(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return new SkinScanResult();
        var variantDirs = new Dictionary<PetAction, List<(int idx, string path)>>();
        var all = Directory.GetDirectories(rootDir);
        foreach (var d in all)
        {
            var name = Path.GetFileName(d);
            var us = name.LastIndexOf('_');
            if (us > 0 && int.TryParse(name[(us+1)..], out var idx)
                && Enum.TryParse<PetAction>(name[..us], true, out var act))
            {
                variantDirs.TryAdd(act, new()); variantDirs[act].Add((idx, d));
            }
        }
        var result = new SkinScanResult();
        foreach (var (act, list) in variantDirs)
        {
            var variants = new List<List<string>>();
            foreach (var (_, path) in list.OrderBy(x => x.idx))
            {
                var urls = ScanFrames(path);
                if (urls.Count > 0)
                {
                    variants.Add(urls);
                    result.Sounds.TryAdd(act, ScanSound(path));
                }
            }
            if (variants.Count > 0) result.VariantUrls[act] = variants;
        }
        foreach (var act in Enum.GetValues<PetAction>())
        {
            if (result.VariantUrls.ContainsKey(act)) continue;
            var dir = Path.Combine(rootDir, act.FolderName());
            var urls = ScanFrames(dir);
            if (urls.Count > 0)
            {
                result.VariantUrls[act] = new() { urls };
                result.Sounds[act] = ScanSound(dir);
            }
        }
        result.VariantUrls = result.VariantUrls
            .OrderBy(kv => (int)kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        return result;
    }

    private static List<string> ScanFrames(string dir)
        => Directory.Exists(dir)
            ? Directory.GetFiles(dir).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, NaturalComparer.Instance).ToList()
            : new List<string>();
    private static string? ScanSound(string dir)
    {
        foreach (var ext in new[]{"m4a","mp3","wav","aiff"})
        { var f = Path.Combine(dir, "sound." + ext); if (File.Exists(f)) return f; }
        return null;
    }
}
```
（`NaturalComparer` 用 `StrCmpLogicalW` P/Invoke 或字母序兜底；实现到 `DeskPet.Core/Text/NaturalComparer.cs`，Task 4 Step 3 一并交付。）

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): port skin directory scanner with variant resolution"
```

---

### Task 5: Core 帧签名 — FrameSignatureComputer

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/FrameSignatureComputer.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/FrameSignatureComputerTests.cs`

**Interfaces:**
- Consumes: 无（操作 `byte[]` RGBA 位图）。
- Produces: `static float[] Compute(byte[] rgba, int width, int height)`（16×16 网格，每格亮度 + alpha，共 512 float）；`static float Distance(float[] a, float[] b)`（欧氏距离）；`static int ComputeLoopPoint(float[][] signatures)`（Swift `computeLoopPoint` 逻辑：在 [0, 0.7×N) 内找与末帧距离最小的帧，须优于默认绕回点的 0.5 倍才启用）。Task 6 使用。

- [ ] **Step 1: 写失败测试**

```csharp
public class FrameSignatureComputerTests
{
    [Fact]
    public void Compute_Produces_512_Floats()
    {
        var rgba = new byte[8*8*4]; Array.Fill(rgba, (byte)255);
        var s = FrameSignatureComputer.Compute(rgba, 8, 8);
        Assert.Equal(512, s.Length);
    }
    [Fact]
    public void LoopPoint_Prefers_Last_Similar_Frame()
    {
        // 10 帧：第 8 帧与末帧相同 → loopPoint 应 ≈ 8
        var sigs = Enumerable.Range(0, 10).Select(i => RandomFill(1234 + i)).ToArray();
        sigs[8] = sigs[9].ToArray();
        var lp = FrameSignatureComputer.ComputeLoopPoint(sigs);
        Assert.Equal(8, lp);
    }
    private static float[] RandomFill(int seed) { var r = new Random(seed); return Enumerable.Range(0,512).Select(_ => (float)r.NextDouble()).ToArray(); }
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**（照 Swift `frameSignature` / `computeLoopPoint` / `signatureDistance` 逐行翻译，网格遍历、alpha 阈值 0.05、`bestDist < defaultDist * 0.5` 等逻辑保持一致）

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): port frame signature and loop-point computation"
```

---

### Task 6: Core 帧引擎 — PetSkin（缓存/预取/流式补齐/loopPoint/节能）

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/PetSkin.cs`
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/IImageDecoder.cs`
- Test: `DeskPet.Windows/tests/DeskPet.Windows.Core.Tests/Pet/PetSkinTests.cs`

**Interfaces:**
- Consumes: `PetAction`（T2）、`SkinScanResult`（T4）、`FrameSignatureComputer`（T5）。
- Produces:
  - `interface IImageDecoder { Task<DecodedFrame?> DecodeAsync(string path, int maxPixel); }`，`record DecodedFrame(int Width, int Height, byte[] Rgba)`。
  - `class PetSkin`：`static Task<PetSkin> LoadAsync(string rootDir, IImageDecoder decoder)`；成员：`int VariantCount(PetAction)`、`int TotalFrameCount(PetAction,int)`、`bool HasRealMaterial(PetAction)`、`Task<IReadOnlyList<DecodedFrame>?> FramesAsync(PetAction, int variant=-1)`、`void Prefetch(IEnumerable<PetAction>)`、`void BackfillIfNeeded(PetAction,int,int currentIndex)`、`void MarkPlayed(PetAction)`、`void TrimToHomeOnly()`、`void ExitHomeOnlyMode()`、`int? PreferredVariant(PetAction)`、`double? Fps(PetAction)`、`int Loops(PetAction)`、`string? Sound(PetAction)`、`(int,int)? BaseFrameSize`。
  - 行为对照 Swift `PetSkin`：部分帧预缓存（trailer=24）、流式补齐（batch=24，距末尾 ≤8 帧触发）、`homeOnlyMode` 节能、`markPlayed` 截断到 trailer、变体选择优先已缓存。内存/淘汰策略保持同语义。

- [ ] **Step 1: 写失败测试（注入 FakeDecoder）**

```csharp
public class PetSkinTests
{
    private sealed class FakeDecoder : IImageDecoder
    {
        private readonly Dictionary<string, DecodedFrame> _map;
        public FakeDecoder(IEnumerable<string> files) { _map = files.ToDictionary(f => f, f => new DecodedFrame(64,64, new byte[64*64*4])); }
        public Task<DecodedFrame?> DecodeAsync(string path, int maxPixel) => Task.FromResult(_map.TryGetValue(path, out var f) ? f : null);
    }
    // 场景 A：部分帧预缓存后 FramesAsync 只返回前 trailer 帧，BackfillIfNeeded 后补全
    // 场景 B：TrimToHomeOnly 后仅保留 home/fall，Prefetch 后恢复
    // 场景 C：VariantCount / TotalFrameCount / PreferredVariant（优先已缓存变体）
    // 场景 D：HasRealMaterial / 兜底动作解析
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现 PetSkin**（逐行移植 `SpriteAnimator.swift`；解码走注入的 `IImageDecoder`；签名/loopPoint 用 `FrameSignatureComputer`；后台解码用 `Task.Run` + 内部 `SemaphoreSlim`/集合去重 loading/backfilling）

- [ ] **Step 4: 运行确认通过**（四个场景全绿）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): port PetSkin frame engine with cache/prefetch/backfill"
```

---

### Task 7: 应用 — WPF 解码器 + 透明置顶宠物窗口 + idle 循环

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/WpfImageDecoder.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/Views/PetWindow.xaml` + `PetWindow.xaml.cs`
- Modify: `DeskPet.Windows/src/DeskPet.Windows/App.xaml.cs`（启动宠物窗口）
- Create: `DeskPet.Windows/src/DeskPet.Core/Pet/PetAnimationLoop.cs`（可单测的帧推进器：给定 action/fps/loopPoint，60fps tick 返回当前帧索引，照 `PetManager.animTick` 帧推进部分）

**Interfaces:**
- Consumes: `PetSkin`（T6）。
- Produces: 运行态：无边框透明置顶窗口展示 `DecodedFrame`（RGBA → `BitmapSource`）；`PetAnimationLoop` 驱动 idle 循环；`WpfImageDecoder` 为 `IImageDecoder` 的 WPF 实现。

- [ ] **Step 1: TDD — PetAnimationLoop 帧推进器**

`PetAnimationLoopTests.cs`：循环动作（按 loopPoint 无缝循环）、一次性动作（播完停末帧）、fps 换算。先红后绿。

- [ ] **Step 2: 实现 WpfImageDecoder**

用 `System.Windows.Media.Imaging.BitmapDecoder` 在后台线程解码 PNG/JPG，按 `maxPixel` 降采样，转 `byte[] RGBA`（长边 ≤ maxPixel，`WpfImageDecoder.BufferDims` 保证确定性）；`ToBitmapSource` 做 RGBA→BGRA 交换生成冻结的 `BitmapSource` 供 WPF `Image` 绑定。

- [ ] **Step 3: 实现 PetWindow（无边框透明置顶）**

```csharp
// PetWindow: WPF Window，属性 WindowStyle=None、AllowsTransparency=True、
// Background=Transparent、Topmost=True、ShowInTaskbar=False、ResizeMode=NoResize
// Image 控件绑定当前帧 BitmapSource；加载失败显示占位提示
// 60fps DispatcherTimer（~16ms）驱动 PetAnimationLoop + PetSkin.FramesAsync 取帧
```

- [ ] **Step 4: 构建运行验证**

Run: `dotnet build` + 启动应用。
Expected: 桌面出现透明置顶宠物，播放 idle 待机动画（对应素材 `BoringPet/idle_0`）。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(windows): pet window with WPF decoder and idle animation loop"
```

---

### Task 8: 应用外壳 — 托盘 + 设置窗口占位

**Files:**
- Create: `DeskPet.Windows/src/DeskPet.Windows/Services/TrayService.cs`
- Create: `DeskPet.Windows/src/DeskPet.Windows/Views/SettingsWindow.xaml` + `SettingsWindow.xaml.cs`
- Modify: `App.xaml.cs`（初始化托盘，托盘项：显示/隐藏、回窝/出窝、设置/退出）

**Interfaces:**
- Consumes: `PetWindow`（T7）。
- Produces: `TrayService`（`System.Windows.Forms.NotifyIcon` 包装：单击显示/隐藏宠物、右键菜单含"回窝/出窝/设置/退出"，文字随 `PetLifeState` 切换）；`SettingsWindow` 占位窗口（左栏导航 + 右栏内容，Phase 3 填充）。

- [ ] **Step 1: 实现 TrayService**（`System.Windows.Forms.NotifyIcon` 混用（csproj 已开 `UseWindowsForms=true`）；图标用内置素材 `home` 帧或应用资源）

- [ ] **Step 2: 实现 SettingsWindow 占位**（WPF `Window`，左侧"宠物/聊天/设置"占位项，右侧页面内容占位）

- [ ] **Step 3: 构建运行验证** — 托盘图标出现，菜单可用，隐藏/显示宠物窗口正常。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(windows): tray service and settings placeholder"
```

---

## Self-Review

**1. Spec coverage（§10 / Phase 0 目标）**
- 解决方案结构 + 免安装便携配置 → Task 1 ✓
- Core 纯逻辑层 + xUnit → Task 1/2 ✓
- 素材拷贝（assets/BoringPet）→ Task 1 Step 1 ✓
- 帧管线（解码抽象 + 缓存 + 流式补齐）→ Task 5/6 ✓
- 透明置顶窗口显示动画 → Task 7 ✓
- 托盘外壳 → Task 8 ✓
- 命名/文件夹与 Swift 对应 → Global Constraints + 各 Task 文件命名 ✓

**2. Placeholder 扫描**
- 无 TBD/TODO；每个 Task 有具体文件、测试断言、实现要点或命令。Task 5/6 的"逐行翻译"以 Swift 源文件为参照（路径在 spec 中给出），非占位。

**3. 类型一致性**
- `PetAction` 在 T2 定义，T3 配置、T4 扫描、T6 PetSkin 均消费同一枚举；`IImageDecoder`/`DecodedFrame` 在 T6 定义，T7 WPF 实现、T6 FakeDecoder 测试，签名一致。`PetSkin` 成员签名在 T6 Produces 中固定，T7/T8 仅按此使用。
- `PetAnimationLoop`（T7）不依赖 `PetSkin` 内部状态，接口清晰。

**待跟进（Phase 1 计划）**：`PetManager` 状态机/行为 AI、`windowPosition` 驱动窗口、单击/双击/拖拽交互、回窝（托盘）流程 — 属 Phase 1，独立计划。
