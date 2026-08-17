using System;
using System.IO;
using System.Reflection;
using I2.Loc;
using UnityEngine;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// </summary>
    public static class BlackSpearmanArt
    {
        public const string TERM_NAME = "BLACKSPEARMAN/NAME";
        public const string TERM_DESC = "BLACKSPEARMAN/DESC";
        const string ICON_FILE = "black_spearman_icon.png";

        static Sprite _icon;
        static bool _localized;

        public static Sprite GetIcon()
        {
            if (_icon != null) return _icon;
            _icon = LoadPngIcon() ?? ProceduralIcon();
            return _icon;
        }

        static Sprite LoadPngIcon()
        {
            try
            {
                byte[] data = null;
                string src = null;

                // ① 外部 PNG（插件目录，可覆盖内嵌资源，便于自定义）
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var candidates = new[]
                {
                    Path.Combine(dir, ICON_FILE),
                    Path.Combine(Path.Combine(dir, "Resources"), ICON_FILE)
                };
                foreach (var p in candidates)
                {
                    if (File.Exists(p)) { data = File.ReadAllBytes(p); src = p; break; }
                }

                // ② 内嵌资源（随 DLL 一起编译，仅部署 DLL 也能显示改色版头像）
                if (data == null)
                {
                    try
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        foreach (var name in asm.GetManifestResourceNames())
                        {
                            if (name.IndexOf(ICON_FILE, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            using (var s = asm.GetManifestResourceStream(name))
                            {
                                if (s == null) continue;
                                data = new byte[s.Length];
                                s.Read(data, 0, data.Length);
                            }
                            src = "(embedded:" + name + ")";
                            break;
                        }
                    }
                    catch { }
                }

                if (data == null) return null;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, data)) return null;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                BSLog.Info("[ART] 头像图标已加载: " + src + " (" + tex.width + "x" + tex.height + ")");
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception e)
            {
                BSLog.Warn("[ART] PNG 图标加载失败: " + e);
                return null;
            }
        }

        static Sprite ProceduralIcon()
        {
            try
            {
                const int size = 64;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var px = new Color32[size * size];
                int c = size / 2;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - c;
                        float dy = y - c;
                        bool inCircle = (dx * dx + dy * dy) <= (size * 0.42f) * (size * 0.42f);
                        if (inCircle)
                            px[y * size + x] = new Color32(24, 24, 28, 255);
                        else
                            px[y * size + x] = new Color32(0, 0, 0, 0);
                        if (Mathf.Abs(x - c) <= 2 && y > size / 4 && y < size * 3 / 4)
                            px[y * size + x] = new Color32(150, 150, 160, 255);
                    }
                }
                tex.SetPixels32(px);
                tex.Apply();
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            }
            catch
            {
                return null;
            }
        }

        public static void RegisterLocalization()
        {
            if (_localized) return;
            _localized = true;
            try
            {
                if (LocalizationManager.Sources == null || LocalizationManager.Sources.Count == 0) return;
                var src = LocalizationManager.Sources[0];
                AddTerm(src, TERM_NAME, "黑色长矛手 (Black Spearman)");
                AddTerm(src, TERM_DESC, "一支被强化过的维京长矛部队，会发动冲刺与刺击。");
                src.UpdateDictionary(false);
            }
            catch (Exception e)
            {
                BSLog.Warn("[ART] 本地化注册失败: " + e);
            }
        }

        static void AddTerm(LanguageSourceData src, string term, string text)
        {
            if (src == null || string.IsNullOrEmpty(term)) return;
            for (int i = 0; i < src.mTerms.Count; i++)
                if (src.mTerms[i] != null && src.mTerms[i].Term == term) return;
            src.AddTerm(term);
            var t = src.mTerms[src.mTerms.Count - 1];
            if (t != null) t.SetTranslation(0, text, null);
        }
    }
}
