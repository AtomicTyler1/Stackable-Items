using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FishNet.Object.Synchronizing;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace StackableItems
{
    [BepInAutoPlugin]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; } = null!;
        private Harmony harmony = null!;

        public static ConfigEntry<int> MaxStackSize;

        private void Awake()
        {
            Log = Logger;
            MaxStackSize = Config.Bind("General", "Max Stack Size", 64, new ConfigDescription("The maximum amount of objects you can have in a given slot. The more you have, the more data can be tracked per player.", new AcceptableValueRange<int>(1, 100)));

            harmony = new Harmony(Id);
            harmony.PatchAll();
        }
    }

    public static class StackManager
    {
        public static int MaxStackSize => Plugin.MaxStackSize.Value;
        public static Item ItemBeingStored;
        public static byte LastRemovedSlot = 255;

        public static Dictionary<ulong, Dictionary<byte, List<Item>>> ServerStacks = new Dictionary<ulong, Dictionary<byte, List<Item>>>();
        public static Dictionary<ulong, Dictionary<byte, List<Item>>> ClientStacks = new Dictionary<ulong, Dictionary<byte, List<Item>>>();

        private static ulong GetSteamID(PlayerInventory inv)
        {
            return inv._player?.SteamID ?? 0;
        }

        public static int GetStackCount(PlayerInventory inv, byte index)
        {
            ulong steamID = GetSteamID(inv);
            if (steamID == 0) return 0;

            if (inv.IsServerInitialized && ServerStacks.TryGetValue(steamID, out var sStacks) && sStacks.TryGetValue(index, out var sList))
            {
                return sList.Count;
            }

            if (inv.Owner != null && inv.Owner.IsLocalClient && ClientStacks.TryGetValue(steamID, out var cStacks) && cStacks.TryGetValue(index, out var cList))
            {
                return cList.Count;
            }

            return 0;
        }

        public static void PushServerStack(PlayerInventory inv, byte index, Item item)
        {
            ulong steamID = GetSteamID(inv);
            if (steamID == 0) return;

            if (!ServerStacks.TryGetValue(steamID, out var stacks))
            {
                stacks = new Dictionary<byte, List<Item>>();
                ServerStacks[steamID] = stacks;
            }

            if (!stacks.TryGetValue(index, out var list))
            {
                list = new List<Item>();
                stacks[index] = list;
            }

            if (list.Count < MaxStackSize - 1)
            {
                list.Add(item);
            }
        }

        public static Item PopServerStack(PlayerInventory inv, byte index)
        {
            ulong steamID = GetSteamID(inv);
            if (steamID == 0) return null;

            if (ServerStacks.TryGetValue(steamID, out var stacks) && stacks.TryGetValue(index, out var list) && list.Count > 0)
            {
                Item item = list[list.Count - 1];
                list.RemoveAt(list.Count - 1);
                return item;
            }

            return null;
        }

        public static void SyncClientStack(PlayerInventory inv, byte index, Item newItem, Item oldItem)
        {
            ulong steamID = GetSteamID(inv);
            if (steamID == 0) return;

            if (!ClientStacks.TryGetValue(steamID, out var stacks))
            {
                stacks = new Dictionary<byte, List<Item>>();
                ClientStacks[steamID] = stacks;
            }

            if (!stacks.TryGetValue(index, out var list))
            {
                list = new List<Item>();
                stacks[index] = list;
            }

            if (newItem == null)
            {
                list.Clear();
            }
            else if (list.Count > 0 && list[list.Count - 1] == newItem)
            {
                list.RemoveAt(list.Count - 1);
            }
            else if (oldItem != null && oldItem != newItem)
            {
                list.Add(oldItem);
            }
        }
    }

    [Serializable]
    public class SavedPlayerStacks
    {
        public ulong SteamID { get; set; }
        public List<SavedItem> StackedItems { get; set; } = new List<SavedItem>();
    }

    [Serializable]
    public class StackSaveFile
    {
        public List<SavedPlayerStacks> Players { get; set; } = new List<SavedPlayerStacks>();
    }

    public static class StackSaveManager
    {
        private static readonly string SaveFolder = Path.Combine(Paths.ConfigPath, "StackableItems", "Saves");
        private static Dictionary<ulong, List<SavedItem>> PendingRestores = new Dictionary<ulong, List<SavedItem>>();

        public static void Save()
        {
            if (SaveManager.CurServerSave == null)
                return;

            foreach (Player player in PlayerManager.Players)
            {
                SavePlayer(player);
            }
        }

        public static void SavePlayer(Player player)
        {
            if (SaveManager.CurServerSave == null)
                return;

            StackSaveFile saveFile = ReadFile(SaveManager.CurServerSave.Name);
            saveFile.Players.RemoveAll(playerStacks => playerStacks.SteamID == player.SteamID);

            SavedPlayerStacks currentStacks = new SavedPlayerStacks
            {
                SteamID = player.SteamID
            };

            if (StackManager.ServerStacks.TryGetValue(player.SteamID, out var slotStacks))
            {
                foreach (KeyValuePair<byte, List<Item>> slotStack in slotStacks)
                {
                    foreach (Item stackedItem in slotStack.Value)
                    {
                        if (stackedItem != null)
                        {
                            currentStacks.StackedItems.Add(SaveManager.ItemToSavedItem(slotStack.Key, stackedItem));
                        }
                    }
                }
            }

            if (currentStacks.StackedItems.Count > 0)
            {
                saveFile.Players.Add(currentStacks);
            }

            Write(SaveManager.CurServerSave.Name, saveFile);
        }

        public static void Load()
        {
            PendingRestores.Clear();

            if (SaveManager.CurServerSave == null)
                return;

            StackSaveFile saveFile = ReadFile(SaveManager.CurServerSave.Name);

            foreach (SavedPlayerStacks playerStacks in saveFile.Players)
            {
                PendingRestores[playerStacks.SteamID] = new List<SavedItem>(playerStacks.StackedItems);
            }
        }

        private static StackSaveFile ReadFile(string serverName)
        {
            string path = GetPath(serverName);

            if (!File.Exists(path))
                return new StackSaveFile();

            try
            {
                string json = File.ReadAllText(path);
                StackSaveFile saveFile = JsonConvert.DeserializeObject<StackSaveFile>(json);
                return saveFile ?? new StackSaveFile();
            }
            catch
            {
                return new StackSaveFile();
            }
        }

        public static List<SavedItem> GetPending(ulong steamID)
        {
            if (!PendingRestores.TryGetValue(steamID, out var items))
                return null;

            PendingRestores.Remove(steamID);
            return items;
        }

        public static void SetStats(Item item, SavedItem savedItem)
        {
            item._cookness.Value = savedItem.Cookness;
            item._bettingMultiplier.Value = savedItem.BettingMultiplier;
            item._killScoreMultiplier.Value = savedItem.KillScoreMultiplier;
            item._curSkin.Value = savedItem.SkinIndex;

            if (item._weapon != null)
            {
                item._weapon._attachments._syncedSight.Value = savedItem.Sight;
                item._weapon._attachments._syncedBarrelAttachment.Value = savedItem.BarrelAttachment;
                item._weapon._attachments._syncedBulletIndex.Value = savedItem.AmmoType;
                item._weapon._attachments._syncedExtendedMag.Value = savedItem.ExtendedMag;
                item._weapon._attachments._syncedLaserSight.Value = savedItem.LaserSight;
            }

            if (item._melee != null)
            {
                item._melee._syncedSharpnessIndex.Value = savedItem.Sharpness;
            }

            if (item._creature != null)
            {
                item._creature._syncedRandomWeight.Value = savedItem.Weight;
            }
        }

        public static void Delete(string serverName)
        {
            string path = GetPath(serverName);
            if (File.Exists(path))
                File.Delete(path);
        }

        private static void Write(string serverName, StackSaveFile saveFile)
        {
            try
            {
                Directory.CreateDirectory(SaveFolder);
                string path = GetPath(serverName);
                string json = JsonConvert.SerializeObject(saveFile, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch { }
        }

        private static string GetPath(string serverName)
        {
            string safeName = serverName;
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(invalidChar, '_');
            }
            return Path.Combine(SaveFolder, safeName + ".json");
        }
    }

    [HarmonyPatch]
    public static class StackableItemsPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ServerTryStoreHeldItem))]
        public static void ServerTryStoreHeldItem_Prefix(Item item)
        {
            StackManager.ItemBeingStored = item;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ServerTryStoreHeldItem))]
        public static void ServerTryStoreHeldItem_Postfix()
        {
            StackManager.ItemBeingStored = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ResolveHeldItemReplacement))]
        public static void ResolveHeldItemReplacement_Prefix(Item item)
        {
            StackManager.ItemBeingStored = item;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ResolveHeldItemReplacement))]
        public static void ResolveHeldItemReplacement_Postfix()
        {
            StackManager.ItemBeingStored = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.LocalTrySelectSlot))]
        public static void LocalTrySelectSlot_Prefix(PlayerInventory __instance)
        {
            StackManager.ItemBeingStored = __instance._player.Holding.HeldItem;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.LocalTrySelectSlot))]
        public static void LocalTrySelectSlot_Postfix()
        {
            StackManager.ItemBeingStored = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.UpdateHeldItem))]
        public static void UpdateHeldItem_Prefix(PlayerInventory __instance)
        {
            StackManager.ItemBeingStored = __instance._player.Holding.HeldItem;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.UpdateHeldItem))]
        public static void UpdateHeldItem_Postfix()
        {
            StackManager.ItemBeingStored = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.GetOpenSlot))]
        public static bool GetOpenSlot(PlayerInventory __instance, int selected, ref int __result)
        {
            if (StackManager.ItemBeingStored != null)
            {
                string targetName = StackManager.ItemBeingStored.name.Replace("(Clone)", "").Trim();
                for (byte b = 0; b < __instance._availableSlots.Count; b++)
                {
                    Item slotItem = __instance._items[b];
                    if (slotItem != null)
                    {
                        string slotName = slotItem.name.Replace("(Clone)", "").Trim();
                        int currentStack = 1 + StackManager.GetStackCount(__instance, b);

                        if (slotName == targetName && currentStack < StackManager.MaxStackSize)
                        {
                            __result = b;
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddItem))]
        public static bool AddItem(PlayerInventory __instance, byte index, Item item)
        {
            if (!__instance.IsServerInitialized || item == null)
                return true;

            Item oldItem = __instance._items[index];

            __instance._items[index] = item;

            if (oldItem != null && oldItem != item)
            {
                StackManager.PushServerStack(__instance, index, oldItem);
            }

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.LoadFromSave))]
        public static void LoadFromSave(PlayerInventory __instance)
        {
            if (!__instance.IsServerInitialized || __instance._player == null)
                return;

            ulong steamID = __instance._player.SteamID;
            List<SavedItem> pending = StackSaveManager.GetPending(steamID);
            if (pending == null || pending.Count == 0)
                return;

            if (!StackManager.ServerStacks.TryGetValue(steamID, out var stacks))
            {
                stacks = new Dictionary<byte, List<Item>>();
                StackManager.ServerStacks[steamID] = stacks;
            }

            foreach (SavedItem savedItem in pending)
            {
                Item spawnable = GameInfo.GetSpawnable(savedItem.ItemID);
                if (!spawnable)
                    continue;

                Item clone = ItemManager.Instance.SpawnNewItem(spawnable, SpawnManager.PlayerSpawnPos, Quaternion.identity);
                StackSaveManager.SetStats(clone, savedItem);

                if (clone.Creature != null)
                {
                    clone.Creature.ServerKillOnSpawn();
                    if (savedItem.IsDripCreature)
                        clone.Creature.SetDrip();
                }

                clone.SetSyncedHolder(__instance._player, forced: true);
                clone.PutInInventory();

                if (!stacks.TryGetValue(savedItem.InventorySlot, out var list))
                {
                    list = new List<Item>();
                    stacks[savedItem.InventorySlot] = list;
                }
                list.Add(clone);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.RemoveItem))]
        public static bool RemoveItem(PlayerInventory __instance, Item item)
        {
            if (!__instance.IsServerInitialized || item == null)
                return true;

            byte foundKey = 255;
            foreach (var kvp in __instance._items)
            {
                if (kvp.Value == item)
                {
                    foundKey = kvp.Key;
                    break;
                }
            }

            StackManager.LastRemovedSlot = foundKey;

            if (foundKey != 255)
            {
                Item nextItem = StackManager.PopServerStack(__instance, foundKey);
                if (nextItem != null)
                {
                    __instance._items[foundKey] = nextItem;
                    return false;
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.RemoveItem))]
        public static void RemoveItem_Postfix(PlayerInventory __instance, Item item)
        {
            if (StackManager.LastRemovedSlot != 255 && __instance.Owner.IsLocalClient)
            {
                __instance.ApplySlot(StackManager.LastRemovedSlot);
                StackManager.LastRemovedSlot = 255;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Item), nameof(Item.DestroyByEating))]
        public static void EatItem(Item __instance)
        {
            var inventory = __instance.LastHolder?.Inventory;
            if (inventory != null)
                inventory.ApplySlot(inventory._localCurSlot);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ServerDropAll))]
        public static void ServerDropAll(PlayerInventory __instance, Vector3 pos, Quaternion rot)
        {
            if (!__instance.IsServerInitialized)
                return;

            ulong steamID = __instance._player?.SteamID ?? 0;
            if (steamID == 0) return;

            if (StackManager.ServerStacks.TryGetValue(steamID, out var stacks))
            {
                Vector3 zero = Vector3.zero;
                foreach (var kvp in stacks)
                {
                    foreach (var hiddenItem in kvp.Value)
                    {
                        if (hiddenItem != null)
                        {
                            zero += Vector3.up * hiddenItem.ModelHeight;
                            hiddenItem.SetSyncedHolder(null);

                            if (!hiddenItem.RigidbodySync.IsSimulatedLocal)
                            {
                                hiddenItem.RigidbodySync.StartSimulateLocal(pos + zero, rot);
                            }
                            else
                            {
                                hiddenItem.transform.position = pos + zero;
                                hiddenItem.Rig.linearVelocity = Vector3.zero;
                                hiddenItem.Rig.angularVelocity = Vector3.zero;
                            }
                            zero += Vector3.up * hiddenItem.ModelHeight;
                        }
                    }
                    kvp.Value.Clear();
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.OnItemsChange))]
        public static void OnItemsChange(PlayerInventory __instance, SyncDictionaryOperation op, byte index, Item item, bool asServer)
        {
            if (asServer || op != SyncDictionaryOperation.Set)
                return;

            if (__instance.Owner != null && __instance.Owner.IsLocalClient)
            {
                Item oldItem = __instance._itemSlots[index].Item;
                StackManager.SyncClientStack(__instance, index, item, oldItem);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.OnStopServer))]
        public static void OnStopServer(PlayerInventory __instance)
        {
            if (__instance._player != null)
            {
                StackSaveManager.SavePlayer(__instance._player);
                ulong steamID = __instance._player.SteamID;
                StackManager.ServerStacks.Remove(steamID);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.OnStopClient))]
        public static void OnStopClient(PlayerInventory __instance)
        {
            ulong steamID = __instance._player?.SteamID ?? 0;
            if (steamID != 0)
                StackManager.ClientStacks.Remove(steamID);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventorySlot), nameof(InventorySlot.SetItem))]
        public static void SetItem(InventorySlot __instance, Item item)
        {
            if (item == null)
            {
                __instance._itemText.text = "";
                return;
            }

            PlayerInventory localInv = Player.LocalPlayer.Inventory;
            if (localInv == null)
                return;

            byte slotIndex = 255;
            for (byte i = 0; i < localInv._itemSlots.Count; i++)
            {
                if (localInv._itemSlots[i] == __instance)
                {
                    slotIndex = i;
                    break;
                }
            }

            if (slotIndex != 255)
            {
                int totalCount = StackManager.GetStackCount(localInv, slotIndex) + 1;
                if (totalCount > 1)
                {
                    if (item.Mesh == null)
                        __instance._itemText.text = $"{item.name} ({totalCount}x)";
                    else
                    {
                        __instance._itemText.text = $"{totalCount}x";
                        __instance._itemText.color = Color.white;
                    }
                    __instance._itemText.gameObject.SetActive(true);
                }
                else if (item.Mesh != null)
                {
                    __instance._itemText.text = "";
                }
                __instance._itemText.transform.SetAsLastSibling();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveServer))]
        public static void SaveServer()
        {
            StackSaveManager.Save();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.OnServerLoaded))]
        public static void OnServerLoaded()
        {
            StackSaveManager.Load();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteServer))]
        public static void DeleteServer()
        {
            if (SaveManager.CurServerSave != null)
                StackSaveManager.Delete(SaveManager.CurServerSave.Name);
        }
    }
}