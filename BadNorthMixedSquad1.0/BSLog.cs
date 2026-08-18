using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 统一日志系统：BepInEx 控制台 + 独立诊断文件 + 全局异常捕获，游戏崩溃也能留下现场。
    /// 分级方法语义（防混淆）：
    /// · <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> —— 写日志文件（带 [时间][级别] 前缀）+ BepInEx 控制台（对应 LogInfo/LogWarning/LogError）；
    /// · <see cref="Diag"/> —— 写日志文件 + BepInEx 控制台（LogInfo，不带级别前缀；给"要贴日志"的临时诊断用）；
    /// · <see cref="Raw"/> —— **只写日志文件、不进控制台**（巨型转储/逐帧明细用，避免刷屏）。
    /// 注意：Info 与 Diag 对控制台的行为相同（都 LogInfo），区别只在是否带 [级别] 前缀；
    /// Raw 是唯一"只写文件"的方法。
    /// </summary>
    public static class BSLog
    {
        static string _logPath;
        static bool _inLogHandler;

        public static string LogPath => _logPath;

        // ============ 诊断开关（由 Plugin 从 cfg Diag 段写入） ============
        // 巨型转储（去剑 ASCII / transform 层级 / SPAWN 首例完整 dump / [头部采样] 逐帧行）→ 默认关，F8 或显式开启
        public static bool VerboseDumps = false;
        // 头部采样追踪（问题①"头部闪白/抽搐"）——保留窗口统计 [头盔统计] 与异常告警，逐帧行见 VerboseDumps
        public static bool HeadTrace = true;
        // 死亡腾空紧凑追踪 + 尸体烘焙探针（问题②"死亡影分身/腾空分裂"）
        public static bool DeathTrace = true;
        // 受击两重分身探针（镜像复启用 / 身体离位 / SpriteRenderer 双重渲染）
        public static bool HitDoubleTrace = true;

        /// <summary>日志分区横幅：把一轮会话内的不同阶段（启动/生成/战斗/死亡）用分隔线组织起来，方便检索。</summary>
        public static void Section(string title)
        {
            try
            {
                Append("\n" + new string('-', 72));
                Append("== " + title + "  (" + DateTime.Now.ToString("HH:mm:ss.fff") + ") ==");
                Append(new string('-', 72));
            }
            catch { }
        }

        public static void Init(string dir)
        {
            try
            {
                _logPath = Path.Combine(dir, "BadNorthBlackSpearman1.3.log");
                File.AppendAllText(_logPath,
                    "\n\n" + new string('=', 80) + "\n" +
                    " Black Spearman v1.3 诊断日志  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                    new string('=', 80) + "\n");

                Application.logMessageReceived += OnLogMessageReceived;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                BSLog.Info("日志系统已初始化: " + _logPath);
            }
            catch { }
        }

        // ============ 全局捕获 ============

        static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (_inLogHandler) return;
            _inLogHandler = true;
            try
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                    Append($"[GAME-{type}] {condition}\n{stackTrace}");
                else if (type == LogType.Warning)
                    Append($"[GAME-WARN] {condition}");
            }
            finally { _inLogHandler = false; }
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Append("[UNHANDLED] " + (e.ExceptionObject as Exception));
        }

        // ============ 分级输出 ============

        /// <summary>常规信息日志：写日志文件（[时间][INFO] 前缀）+ BepInEx 控制台（LogInfo）。</summary>
        public static void Info(string msg) { Write("INFO", msg); }
        /// <summary>警告日志：写日志文件（[时间][WARN] 前缀）+ BepInEx 控制台（LogWarning）。</summary>
        public static void Warn(string msg) { Write("WARN", msg); }
        /// <summary>错误日志：写日志文件（[时间][ERROR] 前缀）+ BepInEx 控制台（LogError）。</summary>
        public static void Error(string msg) { Write("ERROR", msg); }
        /// <summary>裸行：**只写日志文件、不进 BepInEx 控制台**（巨型转储/逐帧明细用，避免刷屏）。</summary>
        public static void Raw(string msg) { Append(msg); }

        /// <summary>诊断行：写日志文件 + BepInEx 控制台（LogInfo，无级别前缀；给"要贴日志"的临时诊断用）。
        /// 注意：BSLog.Raw 只写文件、控制台看不到；需要贴日志时请用 Diag。方法语义总表见类头。</summary>
        public static void Diag(string msg)
        {
            Append(msg);
            try { Plugin.Log?.LogInfo(msg); } catch { }
        }

        static void Write(string level, string msg)
        {
            Append($"[{DateTime.Now:HH:mm:ss.fff}][{level}] {msg}");
            try
            {
                if (level == "ERROR") Plugin.Log?.LogError(msg);
                else if (level == "WARN") Plugin.Log?.LogWarning(msg);
                else Plugin.Log?.LogInfo(msg);
            }
            catch { }
        }

        static void Append(string msg)
        {
            try
            {
                if (!string.IsNullOrEmpty(_logPath))
                    File.AppendAllText(_logPath, msg + "\n");
            }
            catch { }
        }

        /// <summary>
        /// 安全拼接字符串（旧 Mono/CLR2.0 上缺失 string.Join(string, IEnumerable&lt;string&gt;) 重载，
        /// 这里用 StringBuilder 手动实现，兼容 .NET 3.5）。
        /// </summary>
        public static string Join(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var s in items)
            {
                if (!first) sb.Append(", ");
                sb.Append(s);
                first = false;
            }
            return sb.ToString();
        }

        // ============ 反射转储工具 ============

        public static string DumpFields(object obj)
        {
            if (obj == null) return "  <null>";
            var sb = new StringBuilder();
            try
            {
                var t = obj.GetType();
                sb.AppendLine($"  ┌─ {t.FullName}");
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    sb.AppendLine($"  │  {f.FieldType.Name} {f.Name} = {SafeValue(f.GetValue(obj))}");
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    try { sb.AppendLine($"  │  [prop] {p.PropertyType.Name} {p.Name} = {SafeValue(p.GetValue(obj, null))}"); }
                    catch { }
                }
                sb.AppendLine("  └─");
            }
            catch (Exception e) { sb.AppendLine("  DumpFields error: " + e.Message); }
            return sb.ToString();
        }

        static string SafeValue(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object uo)
                return uo ? $"{v.GetType().Name}({uo.name})" : $"{v.GetType().Name}(destroyed)";
            if (v is string s) return "\"" + s + "\"";
            try { return v.ToString(); }
            catch { return v.GetType().Name; }
        }

        public static string DumpHierarchy(GameObject go, int maxDepth = 5)
        {
            if (go == null) return "  <null GameObject>";
            var sb = new StringBuilder();
            DumpTransform(go.transform, sb, 0, maxDepth);
            return sb.ToString();
        }

        static void DumpTransform(Transform t, StringBuilder sb, int depth, int maxDepth)
        {
            if (t == null || depth > maxDepth) return;
            string indent = new string(' ', depth * 2);
            sb.AppendLine(indent + "GameObject: " + t.name + (t.gameObject.activeSelf ? "" : " [inactive]"));
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                sb.AppendLine(indent + "  · " + c.GetType().FullName);
            }
            for (int i = 0; i < t.childCount; i++)
                DumpTransform(t.GetChild(i), sb, depth + 1, maxDepth);
        }
    }
}
