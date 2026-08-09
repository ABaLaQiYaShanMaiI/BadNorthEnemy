using Mono.Cecil;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BadNorthSetup
{
    class Program
    {
        const string NEW_VALUE = "BlackSpearman";
        const int NEW_VAL = 8;
        const string BACKUP_SUFFIX = ".orig_backup";
        const string PLUGIN_DLL = "BlackSpearmanPlugin.dll";
        const string PLUGIN_RES = "BadNorthBlackSpearman1_2.Setup.BlackSpearmanPlugin.dll";

        static string gameDir, dllPath, pluginDest;

        static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "BlackSpearman v1.2 Setup";
            Banner();

            if (!FindGame())
            {
                Console.WriteLine("\n未自动找到 Bad North。请输入安装路径:");
                Console.Write("> ");
                gameDir = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(gameDir) || !File.Exists(
                    Path.Combine(gameDir, "BadNorth.exe")))
                { Console.WriteLine("未找到 BadNorth.exe"); PauseExit(1); }
            }

            dllPath = Path.Combine(gameDir, "BadNorth_Data", "Managed",
                "Assembly-CSharp.dll");
            pluginDest = Path.Combine(gameDir, "BepInEx", "plugins", PLUGIN_DLL);
            Console.WriteLine($"\n游戏: {gameDir}\n");

            while (true)
            {
                bool patched = IsPatched(dllPath);
                bool plugin = File.Exists(pluginDest);
                bool backup = File.Exists(dllPath + BACKUP_SUFFIX);
                Console.WriteLine($"状态: [{(backup?"√":" ")}]备份 [{(patched?"√":" ")}]Patch [{(plugin?"√":" ")}]插件\n");
                Console.WriteLine("[1] 安装  [2] 卸载  [3] 启动游戏  [4] 退出");
                Console.Write("> ");
                switch (Console.ReadKey().KeyChar)
                {
                    case '1': Install(); break;
                    case '2': Restore(); break;
                    case '3': Launch(); break;
                    case '4': return;
                }
                Console.WriteLine();
            }

        static void Install()
        {
            Console.WriteLine("\n=== 安装 ===\n");
            string bak = dllPath + BACKUP_SUFFIX;
            if (!File.Exists(bak)) File.Copy(dllPath, bak);
            else File.Copy(bak, dllPath, true);

            try
            {
                var res = new DefaultAssemblyResolver();
                res.AddSearchDirectory(Path.GetDirectoryName(dllPath));
                using (var a = AssemblyDefinition.ReadAssembly(dllPath,
                    new ReaderParameters { ReadWrite = true, AssemblyResolver = res }))
                {
                    TypeDefinition et = null;
                    foreach (var m in a.Modules)
                        foreach (var t in m.Types)
                            if (t.Name == "VikingAgent" && t.Namespace == "Voxels.TowerDefense")
                                foreach (var n in t.NestedTypes)
                                    if (n.Name == "Type" && n.IsEnum) { et = n; break; }
                    if (et == null) { Console.WriteLine("FAIL: 枚举未找到"); return; }
                    bool ex = false;
                    foreach (var f in et.Fields) if (f.Name == NEW_VALUE) { ex = true; break; }
                    if (!ex)
                    {
                        var nf = new FieldDefinition(NEW_VALUE,
                            Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static |
                            Mono.Cecil.FieldAttributes.Literal | Mono.Cecil.FieldAttributes.HasDefault, et);
                        nf.Constant = NEW_VAL; et.Fields.Add(nf);
                        a.Write(new WriterParameters { WriteSymbols = false });
                        Console.WriteLine("枚举 Patch: OK");
                    }
                    else Console.WriteLine("枚举 Patch: 已存在");
                }
            }
            catch (Exception e) { Console.WriteLine($"FAIL: {e.Message}"); return; }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(PLUGIN_RES))
                {
                    if (s == null) { Console.WriteLine("插件安装: FAIL (未嵌入)"); return; }
                    var dir = Path.GetDirectoryName(pluginDest);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    using (var fs = File.Create(pluginDest)) s.CopyTo(fs);
                    Console.WriteLine("插件安装: OK");
                }
            }
            catch (Exception e) { Console.WriteLine($"插件安装: FAIL: {e.Message}"); return; }

            Console.WriteLine("\n安装完成! 选择 [3] 启动游戏\n");
        }

        static void Restore()
        {
            Console.WriteLine("\n=== 卸载 ===\n");
            string bak = dllPath + BACKUP_SUFFIX;
            if (!File.Exists(bak)) { Console.WriteLine("未找到备份"); return; }
            File.Copy(bak, dllPath, true); Console.WriteLine("DLL 已恢复");
            if (File.Exists(pluginDest)) { File.Delete(pluginDest); Console.WriteLine("插件已移除"); }
            Console.WriteLine("\n卸载完成。备份保留在: " + bak + "\n");
        }

        static void Launch()
        {
            var exe = Path.Combine(gameDir, "BadNorth.exe");
            if (!File.Exists(exe)) { Console.WriteLine("未找到 BadNorth.exe"); return; }
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = gameDir });
        }

        static bool IsPatched(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var a = AssemblyDefinition.ReadAssembly(path,
                    new ReaderParameters { ReadWrite = false }))
                    foreach (var m in a.Modules)
                        foreach (var t in m.Types)
                            if (t.Name == "VikingAgent")
                                foreach (var n in t.NestedTypes)
                                    if (n.Name == "Type")
                                        foreach (var f in n.Fields)
                                            if (f.Name == "BlackSpearman") return true;
            }
            catch { }
            return false;
        }

        static bool FindGame()
        {
            foreach (var p in new[] {
                @"D:\Steam\steamapps\common\BadNorth",
                @"C:\Program Files (x86)\Steam\steamapps\common\BadNorth",
                @"E:\Steam\steamapps\common\BadNorth" })
                if (File.Exists(Path.Combine(p, "BadNorth.exe")))
                { gameDir = p; return true; }
            return false;
        }

        static void Banner()
        {
            Console.WriteLine("========================================");
            Console.WriteLine(" Bad North BlackSpearman v1.2 Setup");
            Console.WriteLine("========================================");
        }

        static void PauseExit(int c) { Console.ReadKey(); Environment.Exit(c); }
    }
}

        }
