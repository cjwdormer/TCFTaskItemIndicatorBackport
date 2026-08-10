using System;
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
    [BepInPlugin("com.thecrimsonfuckr.taskitemindicator", "Task Item Indicator", "1.1.0")]
    public class TaskItemIndicatorPlugin : BaseUnityPlugin
    {
        // quests can complete mid raid, so the wanted set can't be built once at raid start
        private const float QuestRefreshInterval = 2f;

        // all of these were config while tuning, then fixed at the values that felt right in raid.
        // 10m reached too far and 0.08 unlit was still muddy against a bright floor
        private const float TriggerDistance = 5f;
        private const float UnlitLevel = 0.04f;
        private const float DirectionSharpness = 1.5f;
        private const float FadeInFraction = 0.35f;
        private const float FadeSpeed = 7f;

        private ConfigEntry<bool> _modEnabled;
        private ConfigEntry<float> _scanInterval;
        private ConfigEntry<float> _ringScale;
        private ConfigEntry<float> _ringThickness;
        private ConfigEntry<float> _ringOpacity;
        private ConfigEntry<float> _ringColorR;
        private ConfigEntry<float> _ringColorG;
        private ConfigEntry<float> _ringColorB;
        private ConfigEntry<float> _convergeDistance;

        // Quest items/zones currently worth pointing at, rebuilt from the player's active quests every
        // QuestRefreshInterval.
        private readonly HashSet<string> _wanted = new HashSet<string>();
        private readonly HashSet<string> _wantedZones = new HashSet<string>();

        // World position of every PlaceItemTrigger in the scene, keyed by zoneId. See
        // RefreshZonePositions for why the scan/lock-in fields below exist.
        private readonly Dictionary<string, Vector3> _zonePositions = new Dictionary<string, Vector3>();
        private bool _zonesLogged;
        private bool _zonesStable;
        private int _lastZoneScanCount = -1;

        // Ring rendering state.
        private readonly float[] _alpha = new float[4];
        private Image[] _arcs;
        private Canvas _canvas;
        private ActionPanel _actionPanel;

        // Config values the ring was last built with - EnsureRing rebuilds when any of these drift from
        // the live config.
        private int _builtForHeight;
        private float _builtForScale;
        private float _builtForThickness;

        private float _nextScan;
        private float _nextQuestRefresh;

        // Nearest task item/zone found by the most recent scan.
        private bool _hasTarget;
        private Vector3 _targetPosition;
        private float _targetDistance;

        private bool _loggedSenseSpriteState;
        private bool _loggedFirstTarget;
        private bool _loggedTickException;

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

            _ringThickness = Config.Bind(
                "2. Indicator",
                "Ring Thickness",
                3f / 7f,
                new ConfigDescription(
                    "Band width as a fraction of the ring's radius. Higher is thicker; 1.0 is a solid filled disc",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            _ringOpacity = Config.Bind(
                "2. Indicator",
                "Ring Opacity",
                1f,
                new ConfigDescription(
                    "Maximum opacity the ring reaches when fully lit",
                    new AcceptableValueRange<float>(0.1f, 1f)));

            _ringColorR = Config.Bind(
                "2. Indicator",
                "Ring Color R",
                1f,
                new ConfigDescription(
                    "Red component of the ring's tint",
                    new AcceptableValueRange<float>(0f, 1f)));

            _ringColorG = Config.Bind(
                "2. Indicator",
                "Ring Color G",
                1f,
                new ConfigDescription(
                    "Green component of the ring's tint",
                    new AcceptableValueRange<float>(0f, 1f)));

            _ringColorB = Config.Bind(
                "2. Indicator",
                "Ring Color B",
                1f,
                new ConfigDescription(
                    "Blue component of the ring's tint",
                    new AcceptableValueRange<float>(0f, 1f)));

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
            // anything thrown here repeats every frame, so it never gets out - logged once (not every
            // frame) so a silent failure like the Ring Color crash is visible in the log instead of
            // just "the ring never showed up"
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                if (!_loggedTickException)
                {
                    _loggedTickException = true;
                    Logger.LogError("Tick() threw, ring/scanning disabled for the rest of the raid: " + ex);
                }
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

            // Proximity scans and quest refreshes are throttled separately - quests only need
            // rechecking every couple of seconds, everything else follows the scan interval. Bearing and
            // fade still update every frame in UpdateArcs so the ring doesn't feel choppy.
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
        /// Rebuilds <see cref="_wanted"/> and <see cref="_wantedZones"/> from the player's active
        /// quests. Mirrors the check GameWorld.ManageQuestLoot uses to decide whether to spawn a quest
        /// item, rebuilt here since that one only runs once at raid start and quests can complete
        /// mid-raid.
        ///
        /// Written against SPT 4.0.13's client, where QuestController/Quest were renamed (obfuscated
        /// names reshuffle every client build): Player.QuestController is now
        /// AbstractQuestControllerClass, and Quest is now QuestClass. Everything used here - Quests,
        /// QuestStatus, GetConditions, CompletedConditions, ProgressCheckers - kept the same shape under
        /// the new names.
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

                // Mark/place-beacon objectives don't spawn a pickup - the target is a fixed map zone (a
                // PlaceItemTrigger, matched below by zoneId), and the item you place there is already in
                // your inventory. Both condition types share the same zoneId/target shape via ConditionZone.
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

        /// <summary>
        /// Finds the nearest wanted loot item or zone within <paramref name="distance"/> of
        /// <paramref name="from"/> and stores it in <see cref="_hasTarget"/>/<see cref="_targetPosition"/>.
        /// </summary>
        private void FindNearestTaskItem(GameWorld gameWorld, Vector3 from, float distance)
        {
            _hasTarget = false;
            float best = distance * distance;

            // IKillable was renamed IKillableLootItem in SPT 4.0.13's client; LootItem itself (the cast
            // target below) kept its name.
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

                    // QuestItem flags "shows in the task items tab" - ordinary loot a quest happens to
                    // ask for is deliberately not indicated.
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

            // Logged once so a report of "ring never shows" can be told apart from "target was found but
            // the ring itself didn't draw" - the first is a detection bug, the second is a render bug.
            if (_hasTarget && !_loggedFirstTarget)
            {
                _loggedFirstTarget = true;
                Logger.LogInfo("Task item in range at " + _targetDistance.ToString("F1") + "m, ring on.");
            }
        }

        /// <summary>Keeps whichever of the current best and this candidate is closer to <paramref name="from"/>.</summary>
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
        /// Rebuilds <see cref="_zonePositions"/> from every PlaceItemTrigger in the scene - the object
        /// dropped in for mark/place-beacon objectives, whose Id matches a condition's zoneId.
        ///
        /// Some triggers (e.g. content-mod zones) spawn in a few seconds after the raid starts, so a
        /// single scan right at raid start can miss them. FindObjectsOfType is a full scene scan though,
        /// so re-running it for the whole raid would hitch on a big map. This rescans each quest-refresh
        /// tick until the trigger count matches the previous scan - two ticks with no change means the
        /// scene's settled - then stops for good.
        /// </summary>
        private void RefreshZonePositions()
        {
            if (_zonesStable)
            {
                return;
            }

            PlaceItemTrigger[] triggers = FindObjectsOfType<PlaceItemTrigger>();

            if (triggers.Length == _lastZoneScanCount)
            {
                _zonesStable = true;
            }

            _lastZoneScanCount = triggers.Length;

            _zonePositions.Clear();
            foreach (PlaceItemTrigger trigger in triggers)
            {
                _zonePositions[trigger.Id] = trigger.transform.position;
            }

            if (_zonesStable && !_zonesLogged)
            {
                _zonesLogged = true;
                Logger.LogInfo("Task Item Indicator: found " + triggers.Length + " PlaceItemTrigger zone(s) - "
                    + string.Join(", ", _zonePositions.Keys) + ". Wanted zone(s): " + string.Join(", ", _wantedZones)
                    + ". Wanted item(s): " + string.Join(", ", _wanted));
            }
        }

        /// <summary>
        /// Recomputes each arc's target alpha for the current frame and eases the rendered
        /// alpha/color toward it.
        /// </summary>
        private void UpdateArcs(Player player)
        {
            // Only bother checking for an interaction prompt if there's actually a target - the check
            // itself has a cost, and there's nothing to hide the ring behind otherwise.
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
                maxOpacity: _ringOpacity.Value,
                unlitLevel: UnlitLevel,
                directionSharpness: DirectionSharpness,
                fadeInFraction: FadeInFraction);

            Color tint = new Color(_ringColorR.Value, _ringColorG.Value, _ringColorB.Value);
            float step = Time.deltaTime * FadeSpeed;
            for (int i = 0; i < 4; i++)
            {
                _alpha[i] = Mathf.Lerp(_alpha[i], target[i], step);
                if (_arcs != null && _arcs[i] != null)
                {
                    _arcs[i].color = new Color(tint.r, tint.g, tint.b, _alpha[i]);
                }
            }
        }

        /// <summary>Finds and caches the scene's ActionPanel (the interaction-prompt UI).</summary>
        private ActionPanel ResolveActionPanel()
        {
            if (_actionPanel == null)
            {
                _actionPanel = FindObjectOfType<ActionPanel>();
            }

            return _actionPanel;
        }

        // ActionPanel's interaction-buttons container and pointer fields are genuinely private, not just
        // obfuscated names - a private member from another assembly isn't visible to the compiler at
        // all, so accessing them directly fails to build rather than just refusing at runtime.
        // Reflection bypasses that compile-time check the same way Harmony patches do. Cached once -
        // Update() runs every frame and reflection lookups aren't free.
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

        /// <summary>
        /// (Re)builds the ring's Canvas and arc textures if the screen size, scale, or thickness has
        /// changed since the last build.
        /// </summary>
        private void EnsureRing()
        {
            bool sizeChanged = _builtForHeight != Screen.height
                               || !Mathf.Approximately(_builtForScale, _ringScale.Value)
                               || !Mathf.Approximately(_builtForThickness, _ringThickness.Value);

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
            _builtForThickness = _ringThickness.Value;

            int outer = Mathf.Max(3, Mathf.RoundToInt(
                RingGeometry.ReferenceOuterRadius * (Screen.height / RingGeometry.ReferenceScreenHeight) * _ringScale.Value));
            int size = outer * 2;

            // Ring Thickness is band width as a fraction of the outer radius (bigger = thicker) - more
            // intuitive as a slider than the inner/outer radius ratio BuildArcTexture needs, so it's
            // inverted here.
            float innerRadiusFraction = Mathf.Clamp01(1f - _builtForThickness);

            GameObject root = new GameObject("TaskItemIndicator");
            root.transform.SetParent(null);
            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            Color tint = new Color(_ringColorR.Value, _ringColorG.Value, _ringColorB.Value);
            _arcs = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                Texture2D texture = BuildArcTexture(outer, innerRadiusFraction, RingGeometry.SegmentCentres[i]);
                Sprite sprite = Sprite.Create(
                    texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));

                GameObject go = new GameObject("Arc" + i);
                go.transform.SetParent(root.transform, false);

                Image image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.raycastTarget = false;
                image.color = new Color(tint.r, tint.g, tint.b, _alpha[i]);

                RectTransform rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(size, size);

                _arcs[i] = image;
            }
        }

        /// <summary>One arc of the ring, supersampled, with the mid-bright / ends-dimmer taper live has.</summary>
        private static Texture2D BuildArcTexture(int outerRadius, float innerRadiusFraction, float centreDegrees)
        {
            int size = outerRadius * 2;
            int hi = size * RingGeometry.Supersample;
            float centre = hi / 2f - 0.5f;
            float ro = outerRadius * RingGeometry.Supersample;
            float ri = outerRadius * innerRadiusFraction * RingGeometry.Supersample;

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

        /// <summary>Tears down ring state and clears cached scan results when leaving a raid.</summary>
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
            _loggedTickException = false;
            _nextQuestRefresh = 0f;
            _wanted.Clear();
            _wantedZones.Clear();
            _zonePositions.Clear();
            _zonesLogged = false;
            _zonesStable = false;
            _lastZoneScanCount = -1;

            for (int i = 0; i < 4; i++)
            {
                _alpha[i] = 0f;
            }
        }
    }
}
