using System.Reflection;
using System.Threading.Tasks;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using HarmonyLib;

namespace Settings_File_Guard
{
    internal static class PreMainMenuContinueRetryPatches
    {
        private const string HarmonyId = "Settings_File_Guard.PreMainMenuContinueRetryPatches";

        private static readonly MethodInfo s_AutoLoadGuidMethod =
            AccessTools.Method(typeof(GameManager), "AutoLoad", new[] { typeof(Hash128) });
        private static readonly MethodInfo s_AutoLoadAssetMethod =
            AccessTools.Method(typeof(GameManager), "AutoLoad", new[] { typeof(IAssetData) });
        private static readonly MethodInfo s_LoadGuidMethod =
            AccessTools.Method(typeof(GameManager), "Load", new[] { typeof(GameMode), typeof(Purpose), typeof(Hash128) });
        private static readonly MethodInfo s_LoadAssetMethod =
            AccessTools.Method(typeof(GameManager), "Load", new[] { typeof(GameMode), typeof(Purpose), typeof(IAssetData) });
        private static readonly MethodInfo s_LoadDescriptorMethod =
            AccessTools.Method(typeof(GameManager), "Load", new[] { typeof(GameMode), typeof(Purpose), typeof(AsyncReadDescriptor), typeof(Hash128), typeof(System.Guid) });
        private static readonly MethodInfo s_MainMenuMethod =
            AccessTools.Method(typeof(GameManager), "MainMenu", System.Type.EmptyTypes);
        private static readonly MethodInfo s_OnMainMenuReachedMethod =
            AccessTools.Method(typeof(GameManager), "OnMainMenuReached");

        private static Harmony s_Harmony;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            s_Harmony = new Harmony(HarmonyId);
            PatchIfPresent(s_AutoLoadGuidMethod, nameof(AutoLoadGuidPrefix), nameof(LoadTaskPostfix));
            PatchIfPresent(s_AutoLoadAssetMethod, nameof(AutoLoadAssetPrefix), nameof(LoadTaskPostfix));
            PatchIfPresent(s_LoadGuidMethod, nameof(LoadGuidPrefix), nameof(LoadGuidPostfix));
            PatchIfPresent(s_LoadAssetMethod, nameof(LoadAssetPrefix), nameof(LoadAssetPostfix));
            PatchIfPresent(s_LoadDescriptorMethod, nameof(LoadDescriptorPrefix), nameof(LoadDescriptorPostfix));
            PatchIfPresent(s_MainMenuMethod, nameof(MainMenuPrefix), null);
            PatchIfPresent(s_OnMainMenuReachedMethod, null, nameof(OnMainMenuReachedPostfix));
        }

        private static void PatchIfPresent(MethodInfo method, string prefixName, string postfixName)
        {
            if (method == null)
            {
                return;
            }

            HarmonyMethod prefix = prefixName == null ? null : new HarmonyMethod(typeof(PreMainMenuContinueRetryPatches), prefixName);
            HarmonyMethod postfix = postfixName == null ? null : new HarmonyMethod(typeof(PreMainMenuContinueRetryPatches), postfixName);
            s_Harmony.Patch(method, prefix: prefix, postfix: postfix);
        }

        private static void AutoLoadGuidPrefix(Hash128 guid)
        {
            PreMainMenuContinueRetryService.RecordAutoLoadGuid(guid, "GameManager.AutoLoad(Hash128)");
        }

        private static void AutoLoadAssetPrefix(IAssetData asset)
        {
            if (asset == null)
            {
                return;
            }

            PreMainMenuContinueRetryService.RecordLoadGuid(
                GameMode.Game,
                Purpose.LoadGame,
                asset.id.guid,
                $"GameManager.AutoLoad(IAssetData:{asset.name ?? asset.uri ?? "unknown"})");
        }

        private static void LoadGuidPrefix(GameMode mode, Purpose purpose, Hash128 guid)
        {
            PreMainMenuContinueRetryService.RecordLoadGuid(mode, purpose, guid, "GameManager.Load(Hash128)");
        }

        private static void LoadAssetPrefix(GameMode mode, Purpose purpose, IAssetData asset)
        {
            if (asset == null)
            {
                return;
            }

            PreMainMenuContinueRetryService.RecordLoadGuid(
                mode,
                purpose,
                asset.id.guid,
                $"GameManager.Load(IAssetData:{asset.name ?? asset.uri ?? "unknown"})");
        }

        private static void LoadDescriptorPrefix(GameMode mode, Purpose purpose, AsyncReadDescriptor descriptor, Hash128 instigatorGuid, System.Guid sessionGuid)
        {
            PreMainMenuContinueRetryService.RecordLoadGuid(
                mode,
                purpose,
                instigatorGuid,
                $"GameManager.Load(AsyncReadDescriptor:{descriptor.GetType().Name})");
        }

        private static void LoadTaskPostfix(MethodBase __originalMethod, Task<bool> __result)
        {
            if (PreMainMenuContinueRetryService.ShouldIgnorePatchedLoadObservation())
            {
                return;
            }

            PreMainMenuContinueRetryService.ObserveLoadTask(__result, __originalMethod?.Name ?? "unknown", isRetry: false);
        }

        private static void LoadGuidPostfix(GameMode mode, Purpose purpose, Task<bool> __result)
        {
            if (!IsTrackedLoad(mode, purpose) || PreMainMenuContinueRetryService.ShouldIgnorePatchedLoadObservation())
            {
                return;
            }

            PreMainMenuContinueRetryService.ObserveLoadTask(__result, "Load(Hash128)", isRetry: false);
        }

        private static void LoadAssetPostfix(GameMode mode, Purpose purpose, Task<bool> __result)
        {
            if (!IsTrackedLoad(mode, purpose) || PreMainMenuContinueRetryService.ShouldIgnorePatchedLoadObservation())
            {
                return;
            }

            PreMainMenuContinueRetryService.ObserveLoadTask(__result, "Load(IAssetData)", isRetry: false);
        }

        private static void LoadDescriptorPostfix(GameMode mode, Purpose purpose, Task<bool> __result)
        {
            if (!IsTrackedLoad(mode, purpose) || PreMainMenuContinueRetryService.ShouldIgnorePatchedLoadObservation())
            {
                return;
            }

            PreMainMenuContinueRetryService.ObserveLoadTask(__result, "Load(AsyncReadDescriptor)", isRetry: false);
        }

        private static bool MainMenuPrefix(GameManager __instance)
        {
            if (PreMainMenuContinueRetryService.TryInterceptMainMenu(__instance))
            {
                return false;
            }

            return true;
        }

        private static void OnMainMenuReachedPostfix(Purpose purpose, GameMode mode)
        {
            PreMainMenuContinueRetryService.MarkMainMenuReached("GameManager.OnMainMenuReached", purpose, mode);
        }

        private static bool IsTrackedLoad(GameMode mode, Purpose purpose)
        {
            return mode == GameMode.Game && purpose == Purpose.LoadGame;
        }
    }
}
