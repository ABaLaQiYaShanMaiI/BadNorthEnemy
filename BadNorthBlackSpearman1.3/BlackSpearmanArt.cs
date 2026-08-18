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
                Texture2D tex = LoadPng(ICON_FILE);
                if (tex == null) return null;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
            catch (Exception e)
            {
                BSLog.Warn("[ART] 图标加载失败: " + e);
                return null;
            }
        }

        /// <summary>通用 PNG 加载：① 插件目录外部文件（可覆盖内嵌资源、热替换免重编译）→ ② 内嵌资源。
        /// 返回 RGBA32 未压缩 Texture2D（ETC2 免疫），供头像/长矛皮肤等美术资源共用。</summary>
        public static Texture2D LoadPng(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return null;
                byte[] data = null;
                string src = null;

                // ① 外部 PNG（插件目录 + Resources 子目录，可覆盖内嵌资源，便于自定义/热替换）
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var candidates = new[]
                {
                    Path.Combine(dir, fileName),
                    Path.Combine(Path.Combine(dir, "Resources"), fileName)
                };
                foreach (var p in candidates)
                {
                    if (File.Exists(p)) { data = File.ReadAllBytes(p); src = p; break; }
                }

                // ② 内嵌资源（随 DLL 一起编译）
                if (data == null)
                {
                    var asm = Assembly.GetExecutingAssembly();
                    foreach (var name in asm.GetManifestResourceNames())
                    {
                        if (name.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0) continue;
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

                if (data == null) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, data)) return null;
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                BSLog.Info("[ART] 已加载: " + src + " (" + tex.width + "x" + tex.height + ")");
                return tex;
            }
            catch (Exception e)
            {
                BSLog.Warn("[ART] PNG 加载失败 " + fileName + ": " + e);
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
