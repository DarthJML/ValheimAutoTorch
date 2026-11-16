using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DarthJML.AutoTorch
{
    // Keeps torches, sconces, braziers and fires burning by taking fuel from nearby chests.
    //
    // No new pieces and no custom network messages: it does what a player does by hand
    // (take one fuel item out of a chest, press Use on the fire), so it works on any server
    // and nobody else needs the mod.
    //
    // Multiplayer: only the client that owns the fire acts, and it only takes fuel from a chest
    // it owns, that nobody has open, and whose contents are up to date. One actor per fire means
    // no double feeding; fuel is removed first and only the removed amount is added.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class AutoTorchPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.darthjml.autotorch";
        public const string PluginName = "AutoTorch";
        public const string PluginVersion = "1.0.0";

        public static ConfigEntry<bool> Enabled, DebugLog, WoodFires, OnlyAtNight, AlwaysLit;
        public static ConfigEntry<float> Radius, CheckSeconds;
        public static ConfigEntry<int> KeepInChest;
        public static ConfigEntry<string> NeverUse;

        internal static readonly List<Fireplace> Fires = new List<Fireplace>();
        internal static readonly List<Container> Containers = new List<Container>();

        static readonly AccessTools.FieldRef<Fireplace, ZNetView> FireNView = AccessTools.FieldRefAccess<Fireplace, ZNetView>("m_nview");
        static readonly AccessTools.FieldRef<Container, ZNetView> ContainerNView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        static readonly AccessTools.FieldRef<Container, uint> ContainerLastRevision = AccessTools.FieldRefAccess<Container, uint>("m_lastRevision");
        static readonly Func<Container, long, bool> ContainerCheckAccess = AccessTools.MethodDelegate<Func<Container, long, bool>>(AccessTools.Method(typeof(Container), "CheckAccess"));

        float _timer = 110f; // first check about 10 seconds after loading in
        bool _errorLogged;
        string _neverRaw;
        HashSet<string> _never = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Container> _near = new List<Container>();

        void Awake()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Keep fires fuelled from nearby chests.");
            Radius = Config.Bind("General", "Radius", 32f, new ConfigDescription("How far (meters) from each torch or fire to look for a chest with fuel. One floor tile = 2 m, so 32 m = 16 tiles.", new AcceptableValueRange<float>(1f, 64f)));
            CheckSeconds = Config.Bind("General", "CheckSeconds", 120f, new ConfigDescription("Seconds between checks. Fires burn slowly, so a check every couple of minutes is plenty.", new AcceptableValueRange<float>(5f, 600f)));
            OnlyAtNight = Config.Bind("General", "RefuelOnlyAtNight", false, "Only top fires up while it is night. During the day they burn what they have and are not refilled.");
            DebugLog = Config.Bind("General", "Debug", false, "Write a log line for every refuel.");
            WoodFires = Config.Bind("Fuel", "IncludeWoodFires", true, "Also feed fires that burn wood (campfire, hearth, bonfire, hot tub). Turn off to feed only torches, sconces and braziers.");
            KeepInChest = Config.Bind("Fuel", "KeepInChest", 1, new ConfigDescription("Always leave at least this many of the fuel item in each chest, so the chest still counts as the home for that item when sorting.", new AcceptableValueRange<int>(0, 500)));
            NeverUse = Config.Bind("Fuel", "NeverUseTheseItems", "", "Comma-separated fuel items that are never taken from chests, by in-game name or prefab name (for example: Coal, Guck).");
            AlwaysLit = Config.Bind("Cheat", "AlwaysLitNoFuel", false, "Keep every fire full without using any fuel at all.");
            new Harmony(PluginGuid).PatchAll();
            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        void Update()
        {
            if (!Enabled.Value) return;
            _timer += Time.deltaTime;
            if (_timer < CheckSeconds.Value) return;
            _timer = 0f;
            try { Tick(); }
            catch (Exception e)
            {
                if (DebugLog.Value || !_errorLogged) Logger.LogError(e);
                _errorLogged = true;
            }
        }

        static bool CanWrite(Container c)
        {
            if (!c.IsOwner() || c.IsInUse() || c.GetInventory() == null) return false;
            var nview = ContainerNView(c);
            return nview != null && nview.IsValid() && ContainerLastRevision(c) == nview.GetZDO().DataRevision;
        }

        bool Blocked(ItemDrop fuel)
        {
            if (NeverUse.Value != _neverRaw)
            {
                _neverRaw = NeverUse.Value;
                _never = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in (_neverRaw ?? "").Split(','))
                    if (part.Trim().Length > 0) _never.Add(part.Trim());
            }
            if (_never.Count == 0) return false;
            string token = fuel.m_itemData.m_shared.m_name;
            string shown = Localization.instance != null ? Localization.instance.Localize(token) : token;
            return _never.Contains(fuel.gameObject.name) || _never.Contains(shown) || _never.Contains(token);
        }

        void Tick()
        {
            var player = Player.m_localPlayer;
            if (player == null) return; // menu or dedicated server: the client that owns the fire does the work
            if (OnlyAtNight.Value && !AlwaysLit.Value && !EnvMan.IsNight()) return;
            Fires.RemoveAll(f => f == null);
            Containers.RemoveAll(c => c == null);
            long playerId = player.GetPlayerID();
            float r2 = Radius.Value * Radius.Value;

            foreach (var fire in Fires)
            {
                var nview = FireNView(fire);
                if (nview == null || !nview.IsValid() || !nview.IsOwner()) continue;
                if (fire.m_infiniteFuel || !fire.m_canRefill || fire.m_fuelItem == null) continue;
                float fuel = nview.GetZDO().GetFloat(ZDOVars.s_fuel);
                int missing = (int)fire.m_maxFuel - Mathf.CeilToInt(fuel);
                if (missing <= 0) continue;

                if (AlwaysLit.Value)
                {
                    fire.SetFuel(fire.m_maxFuel);
                    if (DebugLog.Value) Logger.LogInfo($"AutoTorch: {fire.name} kept full (AlwaysLitNoFuel)");
                    continue;
                }

                string fuelName = fire.m_fuelItem.m_itemData.m_shared.m_name;
                if (!WoodFires.Value && fuelName == "$item_wood") continue;
                if (Blocked(fire.m_fuelItem)) continue;

                var pos = fire.transform.position;
                _near.Clear();
                foreach (var c in Containers)
                {
                    if ((c.transform.position - pos).sqrMagnitude > r2) continue;
                    if (!CanWrite(c) || !ContainerCheckAccess(c, playerId)) continue;
                    if (c.GetComponentInParent<TombStone>() != null) continue;
                    if (c.GetInventory().CountItems(fuelName) <= KeepInChest.Value) continue;
                    _near.Add(c);
                }
                if (_near.Count == 0) continue;
                _near.Sort((a, b) => (a.transform.position - pos).sqrMagnitude.CompareTo((b.transform.position - pos).sqrMagnitude));

                foreach (var c in _near)
                {
                    if (missing <= 0) break;
                    var inv = c.GetInventory();
                    int have = inv.CountItems(fuelName);
                    int take = Math.Min(missing, have - KeepInChest.Value);
                    if (take <= 0) continue;
                    inv.RemoveItem(fuelName, take);
                    int removed = have - inv.CountItems(fuelName); // add exactly what left the chest
                    for (int i = 0; i < removed; i++) nview.InvokeRPC("RPC_AddFuel");
                    missing -= removed;
                    if (DebugLog.Value && removed > 0)
                        Logger.LogInfo($"AutoTorch: {removed}x {fuelName} from {c.name} -> {fire.name}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
    static class Fireplace_Awake_Patch
    {
        // Placement ghosts have no ZDO; skip them.
        static void Postfix(Fireplace __instance)
        {
            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null) AutoTorchPlugin.Fires.Add(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), "Awake")]
    static class Container_Awake_Patch
    {
        static void Postfix(Container __instance)
        {
            if (__instance.GetInventory() != null) AutoTorchPlugin.Containers.Add(__instance);
        }
    }
}
