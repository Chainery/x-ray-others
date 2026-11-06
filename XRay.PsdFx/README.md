# XRay.PsdFx

.NET Framework **4.5** 轻量级 PSD 图层效果读取器（XRay.Psd 的子集移植）。
只解析文档结构和图层效果，不解码像素；PSD 与 PSB 都支持。

> 新手请先读 **[docs/项目说明.md](docs/项目说明.md)**：快速上手、数据流、
> 为什么必须从头顺序读、真实文件字节偏移示例、属性对照表、常见坑。
>
> 查属性时用 **[docs/效果属性总表.md](docs/效果属性总表.md)**：
> 十种效果的全部属性、描述符键、默认值与钳制、枚举对照、渐变/图案/等高线子对象。

## 为什么有这个项目

`XRay.Psd` 目标 net10.0，无法在 .NET Framework 4.5 上使用。本项目从零实现了
读取效果所需的全部结构（文件头 → 图层记录 → tagged block → Action Manager
描述符），语义与 `XRay.Psd` 保持一致（经合成文件往返比对验证），并且**补上了
投影杂色（Noise，描述符键 `Nose`）**——`XRay.Psd` 目前尚未解析该键
（见 `XRay.Psd/TODO.md`）。

## 能力

- 读取文件头（尺寸、通道、位深、颜色模式，PSB 64 位长度也支持）
- 读取图层记录：名称、可见性、混合模式、不透明度、边界
- 解析**全部十种图层效果**并映射**全部文件属性**：投影/内阴影（杂色、等高线、
  抗锯齿、图层挖空）、外发光/内发光（杂色、范围、光源中心/边缘、精确/柔和、
  渐变）、颜色/渐变/图案叠加（渐变含色标/透明度标/类型/角度/缩放/反向/对齐/
  偏移/平滑度/噪点渐变，图案含 id/名称/缩放/相位/链接）、描边（位置、叠印、
  抖动）、光泽（反向）、斜面浮雕（样式/技法/方向/软化/深度/光泽等高线/等高线/
  等高线范围/纹理及反转/纹理深度），含 Photoshop 5 的老 `lrFX` 格式
- 每个效果同时保留完整的原始 `Descriptor`，任何键都可手动读取
- 原始 tagged block 字节可通过 `layer.GetTaggedBlock(key)` 拿到，便于与
  Photoshop 导出的字节比对

## 用法

```csharp
var document = PsdDocument.FromFile("sample.psd");

foreach (var layer in document.Layers)
{
    if (layer.Style == null) continue;

    foreach (var effect in layer.Style.ActiveEffects)
    {
        Console.WriteLine($"{layer.Name}: {effect}");

        // 杂色：null 表示描述符里没有 Nose 键；0 表示有但为零
        if (effect.Noise.HasValue)
            Console.WriteLine($"  Noise = {effect.Noise.Value * 100:0.#}%");

        // 任意原始键
        double jitter = effect.GetNumber("ShdN", 0);
    }
}
```

## 结构

| 文件 | 职责 |
|---|---|
| `BigEndianReader.cs` | 带边界检查的大端二进制读取器 |
| `Descriptor.cs` | Action Manager 描述符（Objc）解析与访问 |
| `Effects.cs` | 效果模型（`PsdEffect`，全部属性）与 `PsdLayerStyle` 解析 |
| `Contour.cs` | 等高线曲线（ShpC） |
| `Gradient.cs` | 渐变文件属性（色标/透明度标/噪点渐变/摆放） |
| `Pattern.cs` | 图案摆放（id/名称/缩放/相位） |
| `PsdParser.cs` | PSD 结构：文件头、图层记录、tagged block、效果提取 |
| `LegacyEffects.cs` | Photoshop 5 `lrFX` 块翻译成描述符形状 |
| `PsdLayer.cs` / `PsdDocument.cs` | 图层与文档模型 |

## 验证

`tools/XRay.PsdFx.Demo`（同为 net45）在内存里构造一个带"投影(杂色35%) +
内阴影(杂色0%) + 外发光(杂色12%)"的合成 PSD 和一个 Photoshop 5 `lrFX`
图层，读回并打印全部效果：

```powershell
dotnet run --project XRay.PsdFx/tools/XRay.PsdFx.Demo -c Release
```

同一文件的解析结果已与原版 `XRay.Psd`（net10）逐字段比对一致（含渐变、
等高线、纹理、杂色等全部键）。

## 已知限制

- 不解码像素（通道、合并图像）；不做 ICC 颜色管理，灰度/CMYK/HSB 用内置公式
- 渐变只映射文件属性，不做逐像素求值（原库的 ramp/色彩模型插值未移植）
- 图案只映射摆放参数，瓦片像素在全局 `Patt`/`Pat2`/`Pat3` 块中，未解码
- Lab 颜色暂不转换（返回 null）
- `C# 7.3` / net45 语法，未用 records、Span 等现代 API
