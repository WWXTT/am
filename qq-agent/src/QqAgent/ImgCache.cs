using System.IO;
using System.Text.Json.Nodes;

namespace QqAgent;

/// <summary>识图链路（2026-10-25）：UIA 层 pic-element 无任何文件属性（树转储实证只有 Name='图片'），
/// 取图走磁盘缓存。QQNT 把聊天图落在 Documents\Tencent Files\<账号>\nt_qq\nt_data\Pic\<yyyy-MM>\
/// {Ori,OriTemp,Thumb,ThumbTemp}\<md5>.<ext>，消息 ts 与文件 mtime 实测亚秒级对齐
/// （样本 Δ−0.34s，最松 Δ−68s 交给窗口参数覆盖）。
/// 两个实证坑：①扩展名会说谎（Thumb 某 .jpg 实为 PNG）→ 一律魔数判定格式；
/// ②Ori 可能缺席（原图未自动下载只剩缩略图）→ 回落 Thumb_0 / Thumb_720。
/// 只复制绝不动源文件（红线：原文只存不删）。</summary>
internal static class ImgCache
{
    public sealed record Img(string SrcPath, string OutPath, string Stem, string Source,
        string Format, string Ext, long Bytes, DateTimeOffset Mtime, long DeltaMs, int Width = 0, int Height = 0);

    private static readonly (string Dir, string Kind, int Prio)[] SubDirs =
    {
        ("Ori", "Ori", 0), ("OriTemp", "OriTemp", 1), ("Thumb", "Thumb", 2), ("ThumbTemp", "ThumbTemp", 4),
    };

    /// <summary>全部 Pic 根（多账号则多个，本机=1000000001 一个）。空 = 没找到缓存目录。</summary>
    public static List<string> Roots()
    {
        var roots = new List<string>();
        var tf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tencent Files");
        if (!Directory.Exists(tf)) return roots;
        foreach (var acc in Directory.EnumerateDirectories(tf))
        {
            var pic = Path.Combine(acc, "nt_qq", "nt_data", "Pic");
            if (Directory.Exists(pic)) roots.Add(pic);
        }
        return roots;
    }

    /// <summary>时间窗匹配并导出：mtime ∈ [ts−before, ts+after]（全 UTC 比较），按 (|Δt|, 源优先级)
    /// 排序、同 stem 去重（Ori+Thumb 同图只留高优先级源），取前 maxExport 张复制到 outDir。</summary>
    public static List<Img> FindAndExport(long ts, int beforeSec, int afterSec, string? outDir, int maxExport)
    {
        var min = DateTimeOffset.FromUnixTimeSeconds(ts - beforeSec).UtcDateTime;
        var max = DateTimeOffset.FromUnixTimeSeconds(ts + afterSec).UtcDateTime;
        var tUtc = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

        var byStem = new Dictionary<string, (FileInfo Fi, string Source, int Prio, long DeltaMs)>();
        foreach (var root in Roots())
        foreach (var month in MonthDirs(root, ts, beforeSec, afterSec))
        foreach (var (sub, kind, _) in SubDirs)
        foreach (var fi in SafeFiles(month, sub))
        {
            if (Classify(fi.Name, kind) is not { } c) continue;
            var wt = fi.LastWriteTimeUtc;
            if (wt < min || wt > max) continue;
            long delta = (long)(wt - tUtc).TotalMilliseconds;
            var better = !byStem.TryGetValue(c.Stem, out var old)
                || c.Prio < old.Prio || (c.Prio == old.Prio && Math.Abs(delta) < Math.Abs(old.DeltaMs));
            if (better) byStem[c.Stem] = (fi, c.Source, c.Prio, delta);
        }

        return byStem.Values
            .OrderBy(v => Math.Abs(v.DeltaMs)).ThenBy(v => v.Prio)
            .Take(maxExport)
            .Select(v => ExportOne(v.Fi.FullName, v.Source, outDir, v.DeltaMs))
            .OfType<Img>()
            .ToList();
    }

    /// <summary>复制单个缓存文件到 outDir（魔数定扩展名；tmp+move 防半截文件被当成功）。</summary>
    public static Img? ExportOne(string srcPath, string source, string? outDir, long deltaMs)
    {
        try
        {
            var (format, ext) = Sniff(srcPath);
            var stem = StemOf(Path.GetFileName(srcPath));
            var dir = outDir ?? DefaultOutDir();
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, $"{stem}-{source}{ext}");
            if (!File.Exists(dst))
            {
                var tmp = dst + ".tmp";
                File.Copy(srcPath, tmp, overwrite: true);
                File.Move(tmp, dst);
            }
            var fi = new FileInfo(srcPath);
            int iw = 0, ih = 0;
            try
            {
                using var im = System.Drawing.Image.FromFile(srcPath);
                iw = im.Width;
                ih = im.Height;
            }
            catch { }
            return new Img(srcPath, dst, stem, source, format, ext, fi.Length, fi.LastWriteTime, deltaMs, iw, ih);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[img] export fail {srcPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>导出目录：qq-agent 根（含 daemon.bat 的目录）下 logs\img。
    /// 从 exe 位置上溯定位而非依赖 cwd——duty 模板在工程根调 exe。</summary>
    public static string DefaultOutDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "daemon.bat"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "logs", "img");
    }

    /// <summary>月份目录：窗口 [ts−before, ts+after] 换算本地时间后覆盖到的 yyyy-MM（月边界自动多扫）。</summary>
    private static IEnumerable<string> MonthDirs(string root, long ts, int beforeSec, int afterSec)
    {
        var lo = DateTimeOffset.FromUnixTimeSeconds(ts - beforeSec).ToLocalTime();
        var hi = DateTimeOffset.FromUnixTimeSeconds(ts + afterSec).ToLocalTime();
        for (var d = new DateTime(lo.Year, lo.Month, 1); d <= new DateTime(hi.Year, hi.Month, 1); d = d.AddMonths(1))
        {
            var dir = Path.Combine(root, d.ToString("yyyy-MM"));
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>缩略图文件名规格：&lt;md5&gt;_0（小图）/ &lt;md5&gt;_720（720px）；未知规格不参与时间窗匹配，防误配。</summary>
    private static (string Stem, string Source, int Prio)? Classify(string fileName, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        switch (kind)
        {
            case "Ori": return (stem, "Ori", 0);
            case "OriTemp": return (stem, "OriTemp", 1);
            case "ThumbTemp": return (stem, "ThumbTemp", 4);
            case "Thumb":
                if (stem.EndsWith("_0", StringComparison.Ordinal)) return (stem[..^2], "Thumb_0", 2);
                if (stem.EndsWith("_720", StringComparison.Ordinal)) return (stem[..^4], "Thumb_720", 3);
                return null;
            default: return null;
        }
    }

    private static string StemOf(string fileName)
    {
        var s = Path.GetFileNameWithoutExtension(fileName);
        if (s.EndsWith("_720", StringComparison.Ordinal)) return s[..^4];
        if (s.EndsWith("_0", StringComparison.Ordinal)) return s[..^2];
        return s;
    }

    private static string SourceOf(string fullPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(fullPath)!).Name;
        if (dir == "Thumb")
            return Path.GetFileNameWithoutExtension(fullPath).EndsWith("_720", StringComparison.Ordinal)
                ? "Thumb_720" : "Thumb_0";
        return dir;
    }

    /// <summary>魔数判格式（扩展名不可信：实测 Thumb 里 .jpg 实为 PNG）。</summary>
    private static (string Format, string Ext) Sniff(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            int n = fs.Read(head, 0, head.Length);
            if (n >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ("jpeg", ".jpg");
            if (n >= 4 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ("png", ".png");
            if (n >= 4 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38) return ("gif", ".gif");
            if (n >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
                && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return ("webp", ".webp");
            if (n >= 2 && head[0] == 0x42 && head[1] == 0x4D) return ("bmp", ".bmp");
        }
        catch { }
        return ("unknown", ".bin");
    }

    private static IEnumerable<FileInfo> SafeFiles(string monthDir, string sub)
    {
        var dir = Path.Combine(monthDir, sub);
        if (!Directory.Exists(dir)) yield break;
        FileInfo[] files;
        try { files = new DirectoryInfo(dir).GetFiles(); }
        catch { yield break; }
        foreach (var f in files) yield return f;
    }
}
