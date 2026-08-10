using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI;
using System.Reflection;
using TaskItemIndicator.Shared;
using UnityEngine;
using UnityEngine.UI;

namespace TaskItemIndicator
{
    /// <summary>
    /// Backport of live's task item locator (EFT 1.0.5.0). The client has a dormant version of the UI
    /// (ActionPanel.ShowPointer + UIPointer.SenseSprite) but its sprite is unassigned, and ShowPointer is
    /// a bool so it could never carry a direction anyway. So the ring is drawn here instead.
    ///
    /// The arc geometry and brightness math live in TaskItemIndicator.Shared.RingGeometry - plain
    /// floats, no UnityEngine/BepInEx dependency, so it can be unit tested without an SPT install. This
    /// class stays responsible for everything Shared can't do standalone: reading the game state,
    /// BepInEx config, and driving the actual Canvas/Texture2D.
    /// </summary>
    [BepInPlugin("com.thecrimsonfuckr.taskitemindicator", "Task Item Indicator", "1.0.0")]
    public class TaskItemIndicatorPlugin : BaseUnityPlugin
    {
        // quests can complete mid raid, so the wanted set can't be built once at raid start
        private const float QuestRefreshInterval = 2f;

        // all of these were config while tuning, then fixed at the values that felt right in raid.
        // 10m reached too far and 0.08 unlit was still muddy against a bright floor
        private const float TriggerDistance = 5f;
        private const float MaxOpacity = 1f;
        private const float UnlitLevel = 0.04f;
        private const float DirectionSharpness = 1.5f;
        private const float FadeInFraction = 0.35f;
        private const float FadeSpeed = 7f;

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<float> _scanInterval;
        private ConfigEntry<float> _ringScale;
        private ConfigEntry<float> _convergeDistance;

        private readonly HashSet<string> _wanted = new HashSet<string>();
        private readonly HashSet<string> _wantedZones = new HashSet<string>();
        private readonly Dictionary<string, Vector3> _zonePositions = new Dictionary<string, Vector3>();
        private bool _zonesLogged;
        private readonly float[] _alpha = new float[4];
        private Image[] _arcs;
        private Canvas _canvas;
        private ActionPanel _actionPanel;
        private int _builtForHeight;
        private float _builtForScale;

        private float _nextScan;
        private float _nextQuestRefresh;
        private bool _hasTarget;
        private Vector3 _targetPosition;
        private float _targetDistance;
        private bool _loggedSenseSpriteState;
        private bool _loggedFirstTarget;

        private void Awake()
        {
            _modEnabled = Config.Bind(
                "1. General",
                "Enable Mod",
                true,
                "Master toggle - enables or disables the entire mod");

            _ringScale = Config.Bind(
                "2. Indicator",
                "Ring Scale",
                1f,
                new ConfigDescription(
                    "Size multiplier. 1.0 matches live, which is small - about 14px across at 720p",
                    new AcceptableValueRange<float>(0.5f, 4f)));

            _convergeDistance = Config.Bind(
                "2. Indicator",
                "Converge Distance",
                1.5f,
                new ConfigDescription(
                    "How close, in metres, before the whole ring lights up instead of pointing. You have to be looking at the item too",
                    new AcceptableValueRange<float>(0f, 15f)));

            _scanInterval = Config.Bind(
                "2. Indicator",
                "Scan Interval",
                0.25f,
                new ConfigDescription(
                    "Seconds between proximity checks. Direction still updates every frame",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            Logger.LogInfo("Task Item Indicator loaded.");
        }

        private void Update()
        {
            // anything thrown here repeats every frame, so it never gets out
            try
            {
                Tick();
            }
            catch
            {
            }
        }

        private void Tick()
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            Player player = gameWorld == null ? null : gameWorld.MainPlayer;

            if (gameWorld == null || player == null || !_modEnabled.Value)
            {
                LeaveRaid();
                return;
            }

            EnsureRing();

            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + _scanInterval.Value;

                if (Time.time >= _nextQuestRefresh)
                {
                    _nextQuestRefresh = Time.time + QuestRefreshInterval;
                    RefreshWanted(player);
                    RefreshZonePositions();
                    LogSenseSpriteStateOnce();
                }

                FindNearestTaskItem(gameWorld, EyePosition(player), TriggerDistance);
            }

            UpdateArcs(player);
        }

        /// <summary>
        /// Same test GameWorld.ManageQuestLoot runs to decide whether to spawn a quest item for you,
        /// rebuilt here because that one only runs once at raid start.
        ///
        /// SPT 4.0.13's client renamed the old QuestController/Quest types (obfuscated names, reshuffle
        /// each client build): Player.QuestController is now Player.AbstractQuestControllerClass
        /// (type AbstractQuestControllerClass), and Quest is now QuestClass. Everything this method
        /// touches on them - Quests, QuestStatus, GetConditions, CompletedConditions, ProgressCheckers -
        /// kept the same shape, just under the new names.
        /// </summary>
        private void RefreshWanted(Player player)
        {
            _wanted.Clear();
            _wantedZones.Clear();

            AbstractQuestControllerClass questController = player.AbstractQuestControllerClass;
            if (questController?.Quests == null)
            {
                return;
            }

            foreach (QuestClass quest in questController.Quests)
            {
                if (quest.QuestStatus != EQuestStatus.Started)
                {
                    continue;
                }

                foreach (ConditionFindItem condition in quest.GetConditions<ConditionFindItem>(EQuestStatus.AvailableForFinish))
                {
                    if (!IsOutstanding(quest, condition))
                    {
                        continue;
                    }

                    foreach (string target in condition.target)
                    {
                        _wanted.Add(target);
                    }
                }

                // Mark and place-beacon objectives don't spawn a pickup - the target is a fixed zone
                // in the map (a PlaceItemTrigger, matched below by zoneId), and the item you plant
                // there is already in your inventory. Both condition types share the same zoneId/target
                // shape via ConditionZone.
                foreach (ConditionLeaveItemAtLocation condition in quest.GetConditions<ConditionLeaveItemAtLocation>(EQuestStatus.AvailableForFinish))
                {
                    if (IsOutstanding(quest, condition))
                    {
                        _wantedZones.Add(condition.zoneId);
                    }
                }

                foreach (ConditionPlaceBeacon condition in quest.GetConditions<ConditionPlaceBeacon>(EQuestStatus.AvailableForFinish))
                {
                    if (IsOutstanding(quest, condition))
                    {
                        _wantedZones.Add(condition.zoneId);
                    }
                }
            }
        }

        /// <summary>True while a condition is still active work - not already ticked off, not already satisfied.</summary>
        private static bool IsOutstanding(QuestClass quest, Condition condition)
        {
            if (quest.CompletedConditions.Contains(condition.id))
            {
                return false;
            }

            return !(quest.ProgressCheckers.TryGetValue(condition, out ConditionProgressChecker checker) && checker.Test());
        }

        private void FindNearestTaskItem(GameWorld gameWorld, Vector3 from, float distance)
        {
            _hasTarget = false;
            float best = distance * distance;

            // IKillable was renamed IKillableLootItem in SPT 4.0.13's client; LootItem itself (the cast
            // target below) kept its name
            List<IKillableLootItem> lootList = gameWorld.LootList;
            if (lootList != null && _wanted.Count > 0)
            {
                for (int i = 0; i < lootList.Count; i++)
                {
                    LootItem loot = lootList[i] as LootItem;
                    if (loot == null)
                    {
                        continue;
                    }

                    // QuestItem is the "shows in the task items tab" flag - ordinary loot a quest happens
                    // to ask for is deliberately not indicated
                    Item item = loot.Item;
                    if (item == null || !item.QuestItem || !_wanted.Contains(item.StringTemplateId))
                    {
                        continue;
                    }

                    ConsiderTarget(loot.transform.position, from, ref best);
                }
            }

            if (_wantedZones.Count > 0)
            {
                foreach (string zoneId in _wantedZones)
                {
                    if (_zonePositions.TryGetValue(zoneId, out Vector3 position))
                    {
                        ConsiderTarget(position, from, ref best);
                    }
                }
            }

            // separates "detection never fired" from "detected it and the ring still didn't draw"
            if (_hasTarget && !_loggedFirstTarget)
            {
                _loggedFirstTarget = true;
                Logger.LogInfo("Task item in range at " + _targetDistance.ToString("F1") + "m, ring on.");
            }
        }

        private void ConsiderTarget(Vector3 position, Vector3 from, ref float best)
        {
            float sqr = (position - from).sqrMagnitude;
            if (sqr <= best)
            {
                best = sqr;
                _targetPosition = position;
                _targetDistance = Mathf.Sqrt(sqr);
                _hasTarget = true;
            }
        }

        /// <summary>
        /// PlaceItemTrigger is the zone object dropped in the scene for mark/place-beacon objectives;
        /// its Id matches a condition's zoneId. This used to scan once and cache forever, on the
        /// assumption that the raid scene is fully populated by the time MainPlayer exists - but the
        /// log shows that scan firing before HostGameController even reports the raid started, and
        /// before content mods like WTT's zone data (fetched via /wttcommonlib/zones/get) finish
        /// spawning their own PlaceItemTriggers. A zone caught mid-spawn just silently never gets
        /// found. So this now rebuilds every quest-refresh tick (same 2s cadence as RefreshWanted)
        /// instead - cheap for a few dozen static objects, and self-heals once the scene settles.
        /// </summary>
        private void RefreshZonePositions()
        {
            _zonePositions.Clear();

            PlaceItemTrigger[] triggers = FindObjectsOfType<PlaceItemTrigger>();
            foreach (PlaceItemTrigger trigger in triggers)
            {
                _zonePositions[trigger.Id] = trigger.transform.position;
            }

            if (!_zonesLogged)
            {
                _zonesLogged = true;
                Logger.LogInfo("Task Item Indicator: found " + triggers.Length + " PlaceItemTrigger zone(s) - "
                    + string.Join(", ", _zonePositions.Keys) + ". Wanted zone(s): " + string.Join(", ", _wantedZones));
            }
        }

        private void UpdateArcs(Player player)
        {
            // short-circuits the same way the old inline version did: only ask whether a prompt is up
            // if there's actually a target to hide behind it
            bool interactionPromptVisible = _hasTarget && InteractionPromptVisible();

            float bearing = 0f;
            if (_hasTarget)
            {
                // 0 = dead ahead, +90 = to your right, 180 = behind. Flattened, so looking up or down
                // at the item doesn't swing the ring around
                Vector3 toItem = _targetPosition - EyePosition(player);
                toItem.y = 0f;
                Vector3 forward = player.LookDirection;
                forward.y = 0f;

                bearing = (toItem.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f)
                    ? Vector3.SignedAngle(forward, toItem, Vector3.up)
                    : 0f;
            }

            float[] target = RingGeometry.ComputeArcAlphas(
                hasTarget: _hasTarget,
                interactionPromptVisible: interactionPromptVisible,
                bearingDegrees: bearing,
                targetDistance: _targetDistance,
                triggerDistance: TriggerDistance,
                convergeDistance: _convergeDistance.Value,
                maxOpacity: MaxOpacity,
                unlitLevel: UnlitLevel,
                directionSharpness: DirectionSharpness,
                fadeInFraction: FadeInFraction);

            float step = Time.deltaTime * FadeSpeed;
            for (int i = 0; i < 4; i++)
            {
                _alpha[i] = Mathf.Lerp(_alpha[i], target[i], step);
                if (_arcs != null && _arcs[i] != null)
                {
                    _arcs[i].color = new Color(1f, 1f, 1f, _alpha[i]);
                }
            }
        }

        private ActionPanel ResolveActionPanel()
        {
            if (_actionPanel == null)
            {
                _actionPanel = FindObjectOfType<ActionPanel>();
            }

            return _actionPanel;
        }

        // ActionPanel._interactionButtonsContainer and ._pointer are genuinely private fields on the
        // game's own type, not just an obfuscated-looking name - a private member of a type imported
        // from another assembly isn't part of the compiler's member lookup at all (that's why this was
        // CS1061 "does not contain a definition", not CS0122 "inaccessible", when built directly
        // against SPT 4.0.13's client). Reflection sidesteps the compiler's visibility check the same
        // way Harmony patches do; the CLR itself has no problem handing back a private field's value.
        // Cached once since Update() runs every frame and reflection lookup isn't free.
        private static readonly FieldInfo InteractionButtonsContainerField =
            typeof(ActionPanel).GetField("_interactionButtonsContainer", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo PointerField =
            typeof(ActionPanel).GetField("_pointer", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// True while the game is offering an interaction - take, read, open. The client suppresses its
        /// own sense pointer the same way: ShowPointer only applies the sprite when no cursor is up.
        /// </summary>
        private bool InteractionPromptVisible()
        {
            ActionPanel panel = ResolveActionPanel();
            if (panel == null || InteractionButtonsContainerField == null)
            {
                return false;
            }

            RectTransform buttons = InteractionButtonsContainerField.GetValue(panel) as RectTransform;
            return buttons != null && buttons.gameObject.activeInHierarchy;
        }

        /// <summary>
        /// Player.Position is the body bone, so an item on a desk keeps a vertical offset you can never
        /// walk out of. Measuring from the eye makes shelf-height and floor-height items behave alike.
        /// </summary>
        private static Vector3 EyePosition(Player player)
        {
            Transform camera = player.CameraPosition;
            return camera == null ? player.Position : camera.position;
        }

        private void EnsureRing()
        {
            bool sizeChanged = _builtForHeight != Screen.height
                               || !Mathf.Approximately(_builtForScale, _ringScale.Value);

            if (_canvas != null && _arcs != null && !sizeChanged)
            {
                return;
            }

            if (_canvas != null)
            {
                Destroy(_canvas.gameObject);
            }

            _builtForHeight = Screen.height;
            _builtForScale = _ringScale.Value;

            int outer = Mathf.Max(3, Mathf.RoundToInt(
                RingGeometry.ReferenceOuterRadius * (Screen.height / RingGeometry.ReferenceScreenHeight) * _ringScale.Value));
            int size = outer * 2;

            GameObject root = new GameObject("TaskItemIndicator");
            root.transform.SetParent(null);
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            _arcs = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                Texture2D texture = BuildArcTexture(outer, RingGeometry.SegmentCentres[i]);
                Sprite sprite = Sprite.Create(
                    texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));

                GameObject go = new GameObject("Arc" + i);
                go.transform.SetParent(root.transform, false);

                Image image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.raycastTarget = false;
                image.color = new Color(1f, 1f, 1f, _alpha[i]);

                RectTransform rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(size, size);

                _arcs[i] = image;
            }
        }

        /// <summary>One arc of the ring, supersampled, with the mid-bright / ends-dimmer taper live has.</summary>
        private static Texture2D BuildArcTexture(int outerRadius, float centreDegrees)
        {
            int size = outerRadius * 2;
            int hi = size * RingGeometry.Supersample;
            float centre = hi / 2f - 0.5f;
            float ro = outerRadius * RingGeometry.Supersample;
            float ri = outerRadius * RingGeometry.InnerRadiusFraction * RingGeometry.Supersample;

            float[] coverage = new float[size * size];

            for (int y = 0; y < hi; y++)
            {
                for (int x = 0; x < hi; x++)
                {
                    if (RingGeometry.TryGetArcTaper(x, y, centre, ro, ri, centreDegrees, out float taper))
                    {
                        coverage[(y / RingGeometry.Supersample) * size + (x / RingGeometry.Supersample)] += taper;
                    }
                }
            }

            float samples = RingGeometry.Supersample * RingGeometry.Supersample;
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(coverage[i] / samples));
            }

            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>
        /// Records whether the client's own sense pointer was ever usable. It draws nothing in 4.0.13,
        /// which is why this mod renders its own ring - this line is the evidence for that.
        /// </summary>
        private void LogSenseSpriteStateOnce()
        {
            if (_loggedSenseSpriteState)
            {
                return;
            }

            ActionPanel panel = ResolveActionPanel();
            if (panel == null || PointerField == null)
            {
                return;
            }

            _loggedSenseSpriteState = true;

            UIPointer pointer = PointerField.GetValue(panel) as UIPointer;
            if (pointer == null)
            {
                Logger.LogInfo("ActionPanel found, _pointer is null.");
                return;
            }

            Logger.LogInfo(
                "ActionPanel sprites - Hover: " + (pointer.HoverSprite == null ? "null" : pointer.HoverSprite.name)
                + ", Unavailable: " + (pointer.UnavailableSprite == null ? "null" : pointer.UnavailableSprite.name)
                + ", Sense: " + (pointer.SenseSprite == null ? "null" : pointer.SenseSprite.name));
        }

        private void LeaveRaid()
        {
            if (_canvas != null)
            {
                Destroy(_canvas.gameObject);
                _canvas = null;
            }

            _arcs = null;
            _actionPanel = null;
            _builtForHeight = 0;
            _hasTarget = false;
            _loggedSenseSpriteState = false;
            _loggedFirstTarget = false;
            _nextQuestRefresh = 0f;
            _wanted.Clear();
            _wantedZones.Clear();
            _zonePositions.Clear();
            _zonesLogged = false;

            for (int i = 0; i < 4; i++)
            {
                _alpha[i] = 0f;
            }
        }
    }
}
