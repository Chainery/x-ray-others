using XRay.Psd;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;

try
{
    Run(args);
}
catch (Exception ex)
{
    Console.WriteLine("ERROR: " + ex.GetType().FullName + ": " + ex.Message);
    Console.WriteLine(ex.StackTrace);
}

static void Run(string[] args)
{
    if (args.Length == 0)
    {
        Console.WriteLine("usage: XRay.Psd.Demo <file.psd>");
        return;
    }

    var path = args[0];
    Console.WriteLine("Reading " + path + " (" + new FileInfo(path).Length + " bytes)");

    // 加载文档
    using var doc = PsdDocument.Load(path);
    Console.WriteLine($"Document: {doc.Width}x{doc.Height}, {doc.ColorMode}, {doc.Depth} bits");

    foreach (var layer in doc.EnumerateLayersTopDown())
    {
        if (layer.Kind != PsdLayerKind.Adjustment)
            continue;

        Console.WriteLine($"\n[{layer.Path}]  {layer.ContentKey}  opacity={layer.Opacity} visible={layer.IsVisible}");

        // 懒解析：首次访问才解 payload，非调整层为 null
        var adj = layer.Adjustment;
        if (adj is null)
        {
            Console.WriteLine("  (payload 无法解析)");
            continue;
        }

        Console.WriteLine("  " + adj + (adj.HasParameters ? "" : "  (无参数可解析)"));
        Describe(adj);
    }
}

static void Describe(PsdAdjustment adj)
{
    switch (adj.Kind)
    {
        case PsdAdjustmentKind.Levels:
            if (adj.Levels is { } lv)
            {
                Level("master", lv.Master);
                Level("red", lv.Red);
                Level("green", lv.Green);
                Level("blue", lv.Blue);
                Level("black ink", lv.BlackInk);
            }
            break;

        case PsdAdjustmentKind.Curves:
            if (adj.Curves is { } cv)
            {
                Curve("rgb", cv.Rgb);
                Curve("red", cv.Red);
                Curve("green", cv.Green);
                Curve("blue", cv.Blue);
            }
            break;

        case PsdAdjustmentKind.BrightnessContrast:
            if (adj.BrightnessContrast is { } bc)
                Console.WriteLine($"  brightness={bc.Brightness} contrast={bc.Contrast} legacy={bc.UseLegacy}");
            break;

        case PsdAdjustmentKind.Exposure:
            if (adj.Exposure is { } ex)
                Console.WriteLine($"  exposure={ex.ExposureHundredths / 100.0:0.##} stops  offset={ex.OffsetTenThousandths / 10000.0:0.####}  gamma={ex.GammaHundredths / 100.0:0.##}");
            break;

        case PsdAdjustmentKind.Threshold:
            Console.WriteLine($"  level={adj.ThresholdLevel}");
            break;

        case PsdAdjustmentKind.Posterize:
            Console.WriteLine($"  levels={adj.PosterizeLevels}");
            break;

        case PsdAdjustmentKind.ColorBalance:
            if (adj.ColorBalance is { } bal)
            {
                Balance("shadows", bal.Shadows);
                Balance("midtones", bal.Midtones);
                Balance("highlights", bal.Highlights);
                Console.WriteLine($"  preserveLuminosity={bal.PreserveLuminosity}");
            }
            break;

        case PsdAdjustmentKind.HueSaturation:
            if (adj.HueSaturation is { } hs)
            {
                Console.WriteLine($"  master h/s/l={hs.Hue}/{hs.Saturation}/{hs.Lightness}  colorize={hs.Colorize} ({hs.ColorizeHue}/{hs.ColorizeSaturation}/{hs.ColorizeLightness})");
                for (var i = 0; i < hs.Bands.Length; i++)
                {
                    var b = hs.Bands[i];
                    Console.WriteLine($"    band[{i}] {b.OuterStart}-{b.OuterEnd} inner {b.InnerStart}-{b.InnerEnd} h/s/l={b.Hue}/{b.Saturation}/{b.Lightness}");
                }
            }
            break;

        case PsdAdjustmentKind.Vibrance:
            if (adj.Vibrance is { } vb)
                Console.WriteLine($"  vibrance={vb.Vibrance} saturation={vb.Saturation}");
            break;

        case PsdAdjustmentKind.BlackAndWhite:
            if (adj.BlackAndWhite is { } bw)
                Console.WriteLine($"  R={bw.Reds} Y={bw.Yellows} G={bw.Greens} C={bw.Cyans} B={bw.Blues} M={bw.Magentas} tint={bw.Tint}");
            break;

        case PsdAdjustmentKind.ChannelMixer:
            if (adj.ChannelMixer is { } mx)
            {
                Console.WriteLine($"  monochrome={mx.Monochrome}");
                Mix("red", mx.Red);
                Mix("green", mx.Green);
                Mix("blue", mx.Blue);
            }
            break;

        case PsdAdjustmentKind.PhotoFilter:
            if (adj.PhotoFilter is { } pf)
                Console.WriteLine($"  color=({pf.Red:0.###}, {pf.Green:0.###}, {pf.Blue:0.###}) density={pf.Density} preserveLuminosity={pf.PreserveLuminosity}");
            break;

        case PsdAdjustmentKind.SelectiveColor:
            if (adj.SelectiveColor is { } sc)
                Console.WriteLine($"  absolute={sc.Absolute} ranges={sc.Ranges.Length}");
            break;

        case PsdAdjustmentKind.Invert:
            break;

        default:
            // 渐变映射 / 颜色查找：暂只给原始字节
            Console.WriteLine($"  raw {adj.Data.Length} bytes");
            break;
    }
}

static void Level(string name, LevelsRecord r) =>
    Console.WriteLine($"  levels {name,-10} in {r.BlackInput}-{r.WhiteInput} gamma={r.GammaPercent / 100.0:0.00} out {r.BlackOutput}-{r.WhiteOutput}");

static void Curve(string name, CurvePoint[] points) =>
    Console.WriteLine($"  curve {name,-8} " + string.Join(" ", points.Select(p => $"{p.Input}->{p.Output}")));

static void Balance(string name, ColorBalanceRange r) =>
    Console.WriteLine($"  balance {name,-11} C-R={r.CyanRed} M-G={r.MagentaGreen} Y-B={r.YellowBlue}");

static void Mix(string name, MixerChannel m) =>
    Console.WriteLine($"  mixer {name,-6} R={m.Red} G={m.Green} B={m.Blue} const={m.Constant}");
