using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.SpriteMagic;

public class BlackSpearmanMarker : MonoBehaviour { }

namespace BadNorthBlackSpearman1_1
{
    [BepInPlugin("black.spearman.v1.1", "Bad North - Black Spearman v1.1", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource L;
        const string T = "Viking_SwordShield";
        const float DM = 1.6f, KM = 2.5f, AM = 1.3f, SM = 1.05f, RM = 3.5f;
        internal static HashSet<Agent> BA = new HashSet<Agent>();
        Harmony _h; bool _d; int _sn, _md, _ak, _rg;
        const int MW = 120;

        void Awake()
        {
            Instance=this; L=Logger; LogB("v1.1 BBB-style");
            _h=new Harmony("bs.v1.1");
            try{On.Voxels.TowerDefense.GameSetup.Awake+=OnAwake;}catch{}
            var m=typeof(Landing).GetMethod("Spawn",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(m!=null)_h.Patch(m,postfix:new HarmonyMethod(GetType().GetMethod("OnSpawn",BindingFlags.NonPublic|BindingFlags.Static)));
            PatchAtk(); PatchRng();
        }
        void Start(){InvokeRepeating("Poll",2f,2f);InvokeRepeating("Beat",30f,60f);LogB("Ready");}


        void OnAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake o,GameSetup s){o(s);if(_d)return;_d=true;StartCoroutine(ModVR());}
        IEnumerator ModVR()
        {
            for(int i=1;i<=MW;i++){yield return new WaitForSeconds(.5f);try{int n=LevelStateObjectReferences.dict.Count;
            if(n>0&&LevelStateObjectReferences.dict.ContainsKey(T)){
            var vr=LevelStateObjectReferences.dict[T]as VikingReference;if(vr==null){LogFL("No VR",null);yield break;}
            LogI("[BBB] Modifying "+vr.name);
            var vf=typeof(VikingReference).GetField("viking",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            GameObject pf=null;if(vf!=null){var v=vf.GetValue(vr);pf=v as GameObject;if(pf==null&&v is Component c)pf=c.gameObject;}
            if(pf==null&&vr.vikingClone!=null)pf=vr.vikingClone.gameObject;
            if(pf==null){LogFL("No pf",null);yield break;}
            var blk=Instantiate(pf);blk.name="BlackSpearman_Prefab";DontDestroyOnLoad(blk);blk.SetActive(false);
            blk.AddComponent<BlackSpearmanMarker>();
            Recolor(blk.transform);NoSwords(blk.transform);
            if(vf!=null){var va=blk.GetComponent<VikingAgent>();vf.SetValue(vr,va!=null?(object)va:blk);}
            vr.gameObject.SetActive(true);var vcf=typeof(VikingReference).GetField("vikingClone",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(vcf!=null)vcf.SetValue(vr,null);vr.SendMessage("Start",SendMessageOptions.DontRequireReceiver);vr.gameObject.SetActive(false);
            vr.bounty=Mathf.Max(vr.bounty+1,8);
            LogB("[BBB] Viking_SwordShield MODIFIED");yield break;}}
            catch{}}LogFL("Dict never populated",null);
        }
        void Recolor(Transform r){var all=r.GetComponentsInChildren<BatchedSprite>(true);if(all==null)return;var cp=typeof(BatchedSprite).GetProperty("color",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(cp==null)return;foreach(var bs in all){if(bs==null)continue;try{var o=(Color)cp.GetValue(bs,null);cp.SetValue(bs,new Color(o.r,o.g,.01f,o.a),null);}catch{}}}
        int NoSwords(Transform r){int c=0;for(int i=r.childCount-1;i>=0;i--){var ch=r.GetChild(i);var cn=ch.name.ToLower();if(cn.Contains("sword")||cn.Contains("weapon")||cn.Contains("blade")||cn.Contains("r_weapon")||cn.Contains("l_weapon")){ch.gameObject.SetActive(false);c++;continue;}c+=NoSwords(ch);}return c;}


        static void OnSpawn(Landing li){try{if(li==null)return;var sf=typeof(Landing).GetField("shipLoads",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(sf==null)return;var sls=sf.GetValue(li)as System.Collections.IList;if(sls==null)return;foreach(var sl in sls){if(sl==null)continue;var vf=typeof(ShipLoad).GetField("vikingRef",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(vf==null)continue;var vr=vf.GetValue(sl)as VikingReference;if(vr==null||vr.name!=T)continue;var pf=typeof(Landing).GetProperty("spawnedShip",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(pf==null)continue;var sh=pf.GetValue(li,null)as Longship;if(sh==null)continue;var af=typeof(Longship).GetField("agents",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(af==null)continue;var ags=af.GetValue(sh)as System.Collections.IList;if(ags==null)continue;foreach(var a in ags){var ag=a as Agent;if(ag==null||BA.Contains(ag))continue;ag.gameObject.AddComponent<BlackSpearmanMarker>();Instance._sn++;LogB("[SPAWN] "+ag.name+" #"+Instance._sn);Mod(ag);}}}catch(Exception e){LogE("[SPAWN] "+e);}}

        void Poll(){try{var all=FindObjectsOfType<Agent>();if(all==null)return;foreach(var a in all){if(a==null||!a.isViking||BA.Contains(a))continue;if(a.GetComponent<BlackSpearmanMarker>()==null)continue;Instance._sn++;LogB("[POLL] "+a.name+" #"+Instance._sn);Mod(a);}}catch{}}

        internal static void Mod(Agent a){if(a==null||BA.Contains(a))return;BA.Add(a);Instance._md++;a.scale*=SM;var s=a.brain as Swordsman;if(s!=null){Scl(s.damageLevels,DM);Scl(s.knockbackLevels,KM);}var ar=a.GetComponent<Armor>();if(ar!=null){var af=typeof(Armor).GetField("armor",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(af!=null){var o=af.GetValue(ar)as float[];if(o!=null){var cp=new float[o.Length];Array.Copy(o,cp,o.Length);for(int i=0;i<cp.Length;i++)cp[i]*=AM;af.SetValue(ar,cp);}}}var ch=SpearChargeComponent.AddTo(a);if(ch!=null)ch.Setup(a);a.gameObject.AddComponent<SpearStabAction>();try{var s2=a.brain as Swordsman;if(s2!=null){var bf=typeof(Brain).GetField("actions",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(bf!=null){var acts=bf.GetValue(s2)as System.Collections.IList;if(acts!=null){var c1=a.GetComponent<SpearChargeComponent>();if(c1!=null&&!acts.Contains(c1))acts.Add(c1);var c2=a.GetComponent<SpearStabAction>();if(c2!=null&&!acts.Contains(c2))acts.Add(c2);}}}}catch{}}
        static void Scl(float[] ar,float m){if(ar==null)return;for(int i=0;i<ar.Length;i++)ar[i]*=m;}

        void PatchAtk(){try{var m=typeof(Swordsman).GetMethod("GetAttack",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.FlattenHierarchy,null,new[]{typeof(Agent)},null);if(m!=null)_h.Patch(m,new HarmonyMethod(GetType().GetMethod("AP",BindingFlags.NonPublic|BindingFlags.Static)));}catch{}}
        void PatchRng(){try{var p=typeof(Swordsman).GetProperty("range",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(p!=null){var g=p.GetGetMethod(true);if(g!=null)_h.Patch(g,new HarmonyMethod(GetType().GetMethod("RP",BindingFlags.NonPublic|BindingFlags.Static)));}}catch{}}
        static bool AP(Swordsman i,Agent t,ref Attack r){if(!BA.Contains(i.agent))return true;if(t==null){r=default(Attack);return false;}Instance._ak++;try{int lv=i.agent.squad!=null?i.agent.squad.level:0;float d=2.5f,k=1.2f,s=6f;var df=typeof(Swordsman).GetField("damageLevels",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(df!=null){var a=df.GetValue(i)as float[];if(a!=null&&lv<a.Length)d=Mathf.Max(d,a[lv]);}var kf=typeof(Swordsman).GetField("knockbackLevels",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(kf!=null){var a=kf.GetValue(i)as float[];if(a!=null&&lv<a.Length)k=Mathf.Max(k,a[lv]);}var sf=typeof(Swordsman).GetField("stunLevels",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(sf!=null){var a=sf.GetValue(i)as float[];if(a!=null&&lv<a.Length)s=Mathf.Max(s,a[lv]);}Vector3 d2=(t.chestPos-i.agent.chestPos).normalized;d2.y=0f;if(d2.sqrMagnitude<.001f)d2=i.transform.forward;r=new Attack(new AttackSettings(d,k,0f,s),d2,(t.wChestPos+i.agent.wChestPos)/2f,i,i.agent.squad,"Sfx/English/Spear");return false;}catch{return true;}}
        static bool RP(Swordsman i,ref float r){if(!BA.Contains(i.agent))return true;Instance._rg++;r=i.agent.radius*.7f*RM;return false;}

        static void LogB(string m){if(L!=null)L.LogInfo("[BS] ====== "+m+" ======");}
        static void LogFL(string c,Exception e){if(e!=null)L.LogError("[BS] [FAIL:"+c+"] "+e);else L.LogError("[BS] [FAIL:"+c+"]");}
        internal static void LogI(string m){if(L!=null)L.LogInfo("[BS] "+m);}
        internal static void LogW(string m){if(L!=null)L.LogWarning("[BS] "+m);}
        internal static void LogE(string m){if(L!=null)L.LogError("[BS] "+m);}
        void OnDestroy(){try{On.Voxels.TowerDefense.GameSetup.Awake-=OnAwake;}catch{}CancelInvoke();}
        void Beat(){int a=0;foreach(var x in BA)if(x!=null&&x.aliveState?.active==true)a++;LogI("[BEAT] Sn="+_sn+" Md="+_md+" Al="+a+" Ak="+_ak);}
    }
}