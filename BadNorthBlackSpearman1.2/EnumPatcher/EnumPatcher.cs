// ================================================================
// EnumPatcher.cs — BadNorthBlackSpearman v1.2
// 向 Assembly-CSharp.dll 的 VikingAgent.Type 枚举添加 BlackSpearman = 8
// 编译: dotnet build -c Release
// ================================================================

using Mono.Cecil;
using System;
using System.IO;

namespace BadNorthEnumPatcher
{
    class Program
    {
        const string ENUM_TYPENAME = "VikingAgent/Type";
        const string NEW_VALUE_NAME = "BlackSpearman";
        const int NEW_VALUE = 8;
        const string BACKUP_SUFFIX = ".orig_backup";

        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: EnumPatcher.exe <Assembly-CSharp.dll path>");
                return 1;
            }

            string dllPath = args[0];

            try
            {
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"[ERROR] File not found: {dllPath}");
                    return 1;
                }

                // 备份（仅首次）
                string backupPath = dllPath + BACKUP_SUFFIX;
                if (!File.Exists(backupPath))
                {
                    File.Copy(dllPath, backupPath, overwrite: false);
                    Console.WriteLine($"[BACKUP] Created: {Path.GetFileName(backupPath)}");
                }

                // 加载 DLL
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(Path.GetDirectoryName(dllPath));

                using (var asm = AssemblyDefinition.ReadAssembly(dllPath,
                    new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver }))
                {
                    // 定位 VikingAgent/Type 嵌套枚举
                    TypeDefinition enumType = null;
                    foreach (var mod in asm.Modules)
                        foreach (var t in mod.Types)
                            if (t.Name == "VikingAgent" && t.Namespace == "Voxels.TowerDefense")
                                foreach (var n in t.NestedTypes)
                                    if (n.Name == "Type" && n.IsEnum)
                                    { enumType = n; break; }

                    if (enumType == null)
                    {
                        Console.WriteLine("[ERROR] VikingAgent/Type enum not found!");
                        return 2;
                    }
                    Console.WriteLine($"[FOUND] {enumType.FullName}");

                    // 检查是否已 patch
                    foreach (var f in enumType.Fields)
                        if (f.Name == NEW_VALUE_NAME)
                        {
                            Console.WriteLine($"[SKIP] Already patched — no changes needed");
                            return 0;
                        }

                    // 添加新枚举值
                    var newField = new FieldDefinition(NEW_VALUE_NAME,
                        FieldAttributes.Public | FieldAttributes.Static |
                        FieldAttributes.Literal | FieldAttributes.HasDefault, enumType);
                    newField.Constant = NEW_VALUE;
                    enumType.Fields.Add(newField);
                    Console.WriteLine($"[PATCH] Added: {NEW_VALUE_NAME} = {NEW_VALUE}");

                    // numTypes 自动更新（Enum.GetValues 返回 9 个值）
                    Console.WriteLine("[INFO] numTypes auto-updates via Enum.GetValues()");

                    // 保存
                    asm.Write(new WriterParameters { WriteSymbols = false });
                    Console.WriteLine($"[SUCCESS] Patched: {Path.GetFileName(dllPath)}");
                    Console.WriteLine("  VikingAgent.Type.BlackSpearman = 8");
                    Console.WriteLine("  Original preserved in .orig_backup");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
                return 99;
            }
        }
    }
}
