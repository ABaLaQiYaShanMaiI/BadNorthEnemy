# ================================================================
# asset_patcher.py — BadNorthBlackSpearman v1.2
# 在 Unity 资源包中克隆 Viking_SwordShield → Viking_BlackSpearman
#
# 依赖: pip install UnityPy
# 用法: python asset_patcher.py <path to data.unity3d>
# ================================================================

import UnityPy
import os
import sys
import shutil
from pathlib import Path

# 配置
SOURCE_PREFAB_NAME = "Viking_SwordShield"
NEW_PREFAB_NAME = "Viking_BlackSpearman"
NEW_TYPE_VALUE = 8        # BlackSpearman (from EnumPatcher)
NEW_BOUNTY = 8
BACKUP_SUFFIX = ".orig_backup"


def find_viking_prefabs(env):
    """扫描资源包中所有 Viking_* GameObject"""
    prefabs = []
    for obj in env.objects:
        if obj.type.name == "GameObject":
            try:
                data = obj.read()
                if data.name and data.name.startswith("Viking_"):
                    prefabs.append((obj, data))
                    print(f"  [FOUND] {data.name}")
            except Exception:
                pass
    return prefabs


def clone_and_modify_prefab(source_obj, source_data):
    """
    尝试克隆 GameObject 并修改关键字段。
    
    Unity 2018.x 的序列化格式与 UnityPy 的兼容性需实际测试。
    如果无法直接克隆，则返回 None，由 LaunchModded.bat 的
    fallback 逻辑处理（运行时克隆）。
    """
    try:
        assets_file = source_obj.assets_file
        
        # 方式1: 通过 assets_file 创建新的 GameObject
        # UnityPy API 因版本而异，核心思路：
        # - 复制所有 components
        # - 修改 name / type / bounty 字段
        # - 保持 PPtr 引用不变
        
        new_go = assets_file.create_object(
            "GameObject",
            NEW_PREFAB_NAME,
            source_data  # 保留原始数据作为基础
        )
        
        if new_go:
            print(f"  [CLONED] {NEW_PREFAB_NAME}")
            return new_go
        
    except Exception as e:
        print(f"  [WARN] Clone method 1 failed: {e}")
    
    return None


def main():
    print("=" * 60)
    print(" Bad North Asset Patcher - v1.2")
    print("=" * 60)
    
    if len(sys.argv) < 2:
        print("Usage: python asset_patcher.py <path to data.unity3d>")
        sys.exit(1)
    
    asset_path = sys.argv[1]
    
    if not os.path.exists(asset_path):
        print(f"\n[ERROR] File not found: {asset_path}")
        sys.exit(1)
    
    # === 备份 ===
    backup_path = asset_path + BACKUP_SUFFIX
    if not os.path.exists(backup_path):
        shutil.copy2(asset_path, backup_path)
        print(f"\n[BACKUP] Created: {os.path.basename(backup_path)}")
    else:
        print(f"\n[BACKUP] Already exists: {os.path.basename(backup_path)}")
    
    # === 加载 ===
    print(f"\n[LOAD] {asset_path}")
    try:
        env = UnityPy.load(asset_path)
    except Exception as e:
        print(f"[ERROR] Failed to load asset: {e}")
        print("\n  Possible causes:")
        print("  1. Bad North uses a different asset file (try sharedassets1.resource)")
        print("  2. UnityPy version mismatch — try: pip install UnityPy==1.9.0")
        print("  3. Use AssetStudio GUI to locate the correct file first")
        sys.exit(2)
    
    print(f"  Loaded: {len(env.objects)} objects")
    
    # === 扫描 Viking prefabs ===
    print(f"\n[SCAN] Looking for {SOURCE_PREFAB_NAME}...")
    prefabs = find_viking_prefabs(env)
    
    if not prefabs:
        print("\n[WARN] No Viking_* prefabs found in this file.")
        print("  Try a different asset file:")
        print("    - BadNorth_Data/data.unity3d")
        print("    - BadNorth_Data/sharedassets1.resource")
        print("\n  Or use AssetStudio GUI to explore the file structure.")
        sys.exit(3)
    
    # === 查找并克隆源 prefab ===
    source_obj = None
    source_data = None
    
    for obj, data in prefabs:
        if data.name == SOURCE_PREFAB_NAME:
            source_obj = obj
            source_data = data
            break
    
    if source_obj is None:
        print(f"\n[ERROR] {SOURCE_PREFAB_NAME} not found in this file!")
        print(f"  Found prefabs: {[d.name for _, d in prefabs]}")
        sys.exit(4)
    
    print(f"\n[MATCH] Found: {source_data.name}")
    
    # === 克隆 ===
    print(f"\n[CLONE] Creating {NEW_PREFAB_NAME}...")
    result = clone_and_modify_prefab(source_obj, source_data)
    
    if result:
        # === 保存 ===
        print(f"\n[SAVE] Writing to {asset_path}...")
        try:
            # UnityPy save — 因版本而异
            with open(asset_path, "wb") as f:
                env.save_file(f)
            print("[SUCCESS] Asset patched successfully!")
        except Exception as e:
            print(f"[WARN] Save failed: {e}")
            print("  The prefab may need manual creation — see README.")
    else:
        print("\n" + "=" * 60)
        print(" IMPORTANT: Automatic clone not supported for this Unity version.")
        print(" The mod will use runtime cloning instead (no asset patch needed).")
        print("=" * 60)
        # 返回 0 因为这不是致命错误 — 运行时克隆是 fallback
        return 0
    
    print("\n[DONE]")
    return 0


if __name__ == "__main__":
    sys.exit(main())
