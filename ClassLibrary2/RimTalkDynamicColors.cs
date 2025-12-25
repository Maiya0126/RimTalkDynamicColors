using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimTalk.UI;
using RimTalk.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimTalkDynamicColors
{
    // ==========================================
    // 1. 数据结构
    // ==========================================
    public class ColorEntry : IExposable
    {
        public string text = "";
        public Color color = Color.white;
        public bool isBold = false;
        public ColorEntry() { }
        public ColorEntry(string text, Color color, bool isBold = false) { this.text = text ?? ""; this.color = color; this.isBold = isBold; }
        public void ExposeData() { Scribe_Values.Look(ref text, "text", ""); Scribe_Values.Look(ref color, "color", Color.white); Scribe_Values.Look(ref isBold, "isBold", false); }
    }

    public struct StyleData { public Color color; public bool isBold; public StyleData(Color c, bool b) { color = c; isBold = b; } }

    public class LogItem
    {
        public string Timestamp;
        public string PawnName;
        public string Content;
        public string CleanContent;
        public string OriginalContent;
        public Color NameColor;
        public bool NameBold;
        public object SourceObject;
    }

    // ==========================================
    // 2. 设置类
    // ==========================================
    public class DynamicColorSettings : ModSettings
    {
        public List<ColorEntry> nameEntries = new List<ColorEntry>();
        public List<ColorEntry> keywordEntries = new List<ColorEntry>();

        public bool isGlobalEnabled = true;
        public bool showHistoryTab = true;

        public bool enableRimTalkNameColoring = true;
        public bool enableChatColoring = true;
        public bool enableKeywordColoring = true;
        public bool enableBubblesSync = true;
        public bool autoExportHistory = false;
        public string customExportPath = "";

        public float historyWindowOpacity = 0.9f;

        // 生效对象开关
        public bool enableForPrisoners = true;
        public bool enableForSlaves = true;
        public bool enableForGuests = true;
        public bool enableForFriendlies = true;
        public bool enableForEnemies = true;

        // 非人类支持
        public bool enableForNonHumans = false;
        public Color nonHumanDefaultColor = new Color(0.6f, 0.8f, 0.6f, 1f); // 默认淡绿色

        public bool useFactionColor = false;
        public bool autoApplyFavColor = true;
        public bool autoApplyBold = true;

        public bool enableSelfTalkColor = true;
        public bool selfTalkItalic = false;
        public bool selfTalkBold = false;
        public Color selfTalkColor = new Color(0.6f, 0.6f, 0.6f, 1f);

        public bool enableRainbowEffect = true;
        public bool enableBlackWhiteEffect = true;

        private Dictionary<string, StyleData> _combinedCache;
        private Regex _cachedRegexPattern;

        public Dictionary<string, StyleData> GetCombinedCache() { if (_combinedCache == null) RebuildCache(); return _combinedCache; }
        public Regex GetCachedRegex() { if (_combinedCache == null) RebuildCache(); return _cachedRegexPattern; }

        public void RebuildCache()
        {
            DynamicColorMod.ClearCaches();
            _combinedCache = new Dictionary<string, StyleData>();

            // 1. 自定义名字 (最高优先级)
            if (nameEntries != null)
            {
                foreach (var entry in nameEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.text)) continue;
                    _combinedCache[entry.text] = new StyleData(entry.color, entry.isBold);
                }
            }
            else { nameEntries = new List<ColorEntry>(); }

            // 2. 自动扫描地图上的单位
            if (Current.ProgramState == ProgramState.Playing && Current.Game != null && Current.Game.Maps != null)
            {
                try
                {
                    List<Pawn> allPawns = new List<Pawn>();
                    foreach (var map in Current.Game.Maps)
                    {
                        allPawns.AddRange(map.mapPawns.AllPawnsSpawned.Where(p =>
                            p.RaceProps.Humanlike ||
                            (enableForNonHumans && (p.RaceProps.Animal || p.RaceProps.IsMechanoid))
                        ));
                    }

                    foreach (var p in allPawns)
                    {
                        if (p == null || p.Name == null) continue;
                        string pName = p.Name.ToStringShort;
                        if (string.IsNullOrEmpty(pName)) continue;
                        if (_combinedCache.ContainsKey(pName)) continue;

                        // --- 资格检查 ---
                        bool allow = false;
                        if (p.RaceProps.Humanlike)
                        {
                            if (p.IsPrisoner) allow = enableForPrisoners;
                            else if (p.IsSlave) allow = enableForSlaves;
                            else if (p.HostileTo(Faction.OfPlayer)) allow = enableForEnemies;
                            else if (p.Faction != null && !p.Faction.IsPlayer) allow = enableForFriendlies;
                            else if (p.Faction == null) allow = enableForGuests;
                            else allow = true;
                        }
                        else
                        {
                            // 非人类直接看开关
                            allow = enableForNonHumans;
                        }

                        if (!allow) continue;

                        // --- 颜色获取逻辑 ---
                        Color? finalColor = null;
                        bool finalBold = false;

                        // A. 喜爱颜色 (仅限人类)
                        if (p.RaceProps.Humanlike && autoApplyFavColor && p.story != null && p.story.favoriteColor != null)
                        {
                            finalColor = p.story.favoriteColor.color;
                            finalBold = autoApplyBold;
                        }
                        // B. 派系颜色
                        else if (useFactionColor && p.Faction != null)
                        {
                            finalColor = p.Faction.Color;
                            finalBold = false;
                        }
                        // C. 非人类默认颜色
                        else if (!p.RaceProps.Humanlike && enableForNonHumans)
                        {
                            finalColor = nonHumanDefaultColor;
                            finalBold = false;
                        }

                        if (finalColor.HasValue)
                        {
                            _combinedCache[pName] = new StyleData(finalColor.Value, finalBold);
                        }
                    }
                }
                catch (Exception) { }
            }

            // 3. 关键词
            if (keywordEntries != null)
            {
                foreach (var entry in keywordEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.text)) continue;
                    _combinedCache[entry.text] = new StyleData(entry.color, entry.isBold);
                }
            }
            else { keywordEntries = new List<ColorEntry>(); }

            if (_combinedCache.Count > 0)
            {
                var sortedKeys = _combinedCache.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
                string pattern = "(" + string.Join("|", sortedKeys) + ")";
                try { _cachedRegexPattern = new Regex(pattern, RegexOptions.Compiled); }
                catch { _cachedRegexPattern = null; }
            }
            else { _cachedRegexPattern = null; }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref nameEntries, "nameEntries", LookMode.Deep);
            Scribe_Collections.Look(ref keywordEntries, "keywordEntries", LookMode.Deep);
            if (nameEntries == null) nameEntries = new List<ColorEntry>();
            if (keywordEntries == null) keywordEntries = new List<ColorEntry>();

            Scribe_Values.Look(ref isGlobalEnabled, "isGlobalEnabled", true);
            Scribe_Values.Look(ref showHistoryTab, "showHistoryTab", true);

            Scribe_Values.Look(ref enableRimTalkNameColoring, "enableRimTalkNameColoring", true);
            Scribe_Values.Look(ref enableChatColoring, "enableChatColoring", true);
            Scribe_Values.Look(ref enableKeywordColoring, "enableKeywordColoring", true);
            Scribe_Values.Look(ref enableBubblesSync, "enableBubblesSync", true);
            Scribe_Values.Look(ref autoExportHistory, "autoExportHistory", false);
            Scribe_Values.Look(ref customExportPath, "customExportPath", "");
            Scribe_Values.Look(ref historyWindowOpacity, "historyWindowOpacity", 0.9f);

            Scribe_Values.Look(ref enableForPrisoners, "enableForPrisoners", true);
            Scribe_Values.Look(ref enableForSlaves, "enableForSlaves", true);
            Scribe_Values.Look(ref enableForGuests, "enableForGuests", true);
            Scribe_Values.Look(ref enableForFriendlies, "enableForFriendlies", true);
            Scribe_Values.Look(ref enableForEnemies, "enableForEnemies", true);

            // 保存非人类设置
            Scribe_Values.Look(ref enableForNonHumans, "enableForNonHumans", false);
            Scribe_Values.Look(ref nonHumanDefaultColor, "nonHumanDefaultColor", new Color(0.6f, 0.8f, 0.6f, 1f));

            Scribe_Values.Look(ref useFactionColor, "useFactionColor", false);
            Scribe_Values.Look(ref autoApplyFavColor, "autoApplyFavColor", true);
            Scribe_Values.Look(ref autoApplyBold, "autoApplyBold", true);

            Scribe_Values.Look(ref enableSelfTalkColor, "enableSelfTalkColor", true);
            Scribe_Values.Look(ref selfTalkItalic, "selfTalkItalic", false);
            Scribe_Values.Look(ref selfTalkBold, "selfTalkBold", false);
            Scribe_Values.Look(ref selfTalkColor, "selfTalkColor", new Color(0.6f, 0.6f, 0.6f, 1f));

            Scribe_Values.Look(ref enableRainbowEffect, "enableRainbowEffect", true);
            Scribe_Values.Look(ref enableBlackWhiteEffect, "enableBlackWhiteEffect", true);

            if (Scribe.mode == LoadSaveMode.PostLoadInit) RebuildCache();
        }
    }

    // ==========================================
    // 3. Mod 主类
    // ==========================================
    public class DynamicColorMod : Mod
    {
        public static DynamicColorSettings settings;
        public static Pawn CurrentProcessingPawn = null;
        public static Pawn CurrentRecipientPawn = null;
        public static Harmony harmony;

        public static bool IsDrawingBubble = false;
        public static bool IsDrawingChatLog = false;

        public const string RainbowHediffDefName = "RimTalk_RainbowStatus";
        public const string BlackWhiteHediffDefName = "RimTalk_BlackWhiteStatus";
        public static bool hasBubblesMod = false;

        private Vector2 mainScrollPosition = Vector2.zero;
        public static Vector2 logViewerScrollPos = Vector2.zero;
        private string inputNameBuffer = "";
        private string inputKeywordBuffer = "";

        public static List<LogItem> SessionHistory = new List<LogItem>();
        public static HashSet<object> RecordedObjects = new HashSet<object>();

        public static List<FieldInfo> BubblePawnFields = new List<FieldInfo>();
        private static Dictionary<string, Pawn> _nameToPawnCache = new Dictionary<string, Pawn>();

        public DynamicColorMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<DynamicColorSettings>();
            if (settings == null) settings = new DynamicColorSettings();
            if (settings.nameEntries == null) settings.nameEntries = new List<ColorEntry>();
            if (settings.keywordEntries == null) settings.keywordEntries = new List<ColorEntry>();

            harmony = new Harmony("com.rimtalkdynamiccolors.ui");
            harmony.PatchAll();

            LongEventHandler.ExecuteWhenFinished(() => {
                TryPatchInteractionBubbles();
                TryPatchRimTalkSafe();
            });
        }

        private void TryPatchRimTalkSafe()
        {
            try
            {
                Type overlayType = AccessTools.TypeByName("RimTalk.UI.Overlay");
                if (overlayType == null) return;

                MethodInfo targetMethod = AccessTools.Method(overlayType, "DrawMessageLog");
                if (targetMethod != null)
                {
                    harmony.Patch(targetMethod,
                        prefix: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Prefix)),
                        postfix: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Postfix)),
                        transpiler: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Transpiler))
                    );
                    Log.Message("[RimTalk DynamicColors] Successfully patched RimTalk Overlay.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimTalk DynamicColors] Failed to patch RimTalk: {ex}");
            }
        }

        public static void ClearCaches() => _nameToPawnCache.Clear();
        public static void ClearSessionHistory() { SessionHistory.Clear(); RecordedObjects.Clear(); }

        public void TryPatchInteractionBubbles()
        {
            if (hasBubblesMod) return;
            bool modActive = ModLister.GetActiveModWithIdentifier("Jaxe.Bubbles") != null;
            if (!modActive && ModLister.AllInstalledMods.Any(x => x.Active && x.Name.Contains("Interaction Bubbles"))) modActive = true;
            if (!modActive) return;

            try
            {
                Type targetType = FindBubbleType();
                if (targetType != null)
                {
                    BubblePawnFields.Clear();
                    foreach (var field in AccessTools.GetDeclaredFields(targetType))
                    {
                        if (field.FieldType == typeof(Pawn)) BubblePawnFields.Add(field);
                    }
                    MethodInfo drawMethod = AccessTools.Method(targetType, "Draw");
                    MethodInfo getTextMethod = AccessTools.Method(targetType, "get_Text");
                    if (drawMethod != null)
                    {
                        harmony.Patch(drawMethod,
                            prefix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_Draw), nameof(Patch_Bubbles_Bubble_Draw.Prefix)),
                            postfix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_Draw), nameof(Patch_Bubbles_Bubble_Draw.Postfix)));
                        harmony.Patch(drawMethod, transpiler: new HarmonyMethod(typeof(Patch_CommonTranspiler), nameof(Patch_CommonTranspiler.Transpiler)));
                        hasBubblesMod = true;
                        Log.Message($"[RimTalk DynamicColors] Successfully patched Bubbles.");
                    }
                    if (getTextMethod != null) harmony.Patch(getTextMethod, postfix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_GetText), nameof(Patch_Bubbles_Bubble_GetText.Postfix)));
                }
            }
            catch (Exception ex) { Log.Error("[RimTalk DynamicColors] ERROR patching Bubbles: " + ex.ToString()); }
        }

        private Type FindBubbleType()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Bubbles") { Type t = asm.GetType("Bubbles.Bubble"); if (t != null) return t; foreach (var type in asm.GetTypes()) if (type.Name == "Bubble" && AccessTools.Method(type, "Draw") != null) return type; }
            }
            return AccessTools.TypeByName("InteractionBubbles.Bubble");
        }

        public override string SettingsCategory() => "RTDC_ModName".Translate();

        public static string ColorizeStringForBubbles(string input)
        {
            if (settings == null || !settings.enableBubblesSync) return input;
            return ColorizeString(input);
        }

        public static Pawn FindPawnByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string cleanName = Regex.Replace(name, @"<.*?>", "");

            if (_nameToPawnCache.TryGetValue(cleanName, out Pawn cachedPawn)) return cachedPawn;
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Current.Game.Maps == null) return null;
            List<Pawn> pawns = PawnsFinder.AllMaps_FreeColonistsSpawned;
            if (pawns == null) return null;
            foreach (var p in pawns) { if (p != null && p.Name != null && p.Name.ToStringShort == cleanName) { _nameToPawnCache[cleanName] = p; return p; } }
            _nameToPawnCache[cleanName] = null;
            return null;
        }

        public static string ColorizeString(string input) => ColorizeStringInternal(input, false);

        public static string ColorizeStringInternal(string input, bool forbidEffects)
        {
            if (settings == null || !settings.isGlobalEnabled) return input;
            if (string.IsNullOrEmpty(input)) return input;

            Pawn targetPawn = CurrentProcessingPawn;
            if (!IsDrawingBubble && targetPawn == null)
            {
                Match match = Regex.Match(input, @"^\[(.*?)\]");
                if (match.Success) targetPawn = FindPawnByName(match.Groups[1].Value);
            }

            if (!forbidEffects && targetPawn != null && !targetPawn.Dead && targetPawn.health != null)
            {
                var hediffSet = targetPawn.health.hediffSet;
                if (hediffSet != null)
                {
                    if (settings.enableRainbowEffect && hediffSet.HasHediff(HediffDef.Named(RainbowHediffDefName)))
                        return GenerateRainbowText(input);
                    if (settings.enableBlackWhiteEffect && hediffSet.HasHediff(HediffDef.Named(BlackWhiteHediffDefName)))
                        return GenerateBlackWhiteText(input);
                }
            }

            string result = input;

            if (settings.enableSelfTalkColor)
            {
                string pattern = @"(\(.*?\)|（.*?）|\*.*?\*|【.*?】)";
                result = Regex.Replace(result, pattern, (Match m) =>
                {
                    if (IsLikelyKaomoji(m.Value)) return m.Value;

                    Color finalColor = settings.selfTalkColor;
                    string hex = ColorUtility.ToHtmlStringRGBA(finalColor);
                    string styledPart = $"<color=#{hex}>{m.Value}</color>";

                    if (settings.selfTalkItalic) styledPart = $"<i>{styledPart}</i>";
                    if (settings.selfTalkBold) styledPart = $"<b>{styledPart}</b>";

                    return styledPart;
                });
            }

            Regex cachedRegex = settings.GetCachedRegex();
            Dictionary<string, StyleData> cache = settings.GetCombinedCache();

            if (cachedRegex != null && cache != null)
            {
                result = cachedRegex.Replace(result, (Match m) => {
                    string key = m.Value;
                    if (key.StartsWith("<")) return key;
                    if (cache.TryGetValue(key, out StyleData style))
                    {
                        string hex = ColorUtility.ToHtmlStringRGB(style.color);
                        string replacement = $"<color=#{hex}>{key}</color>";
                        if (style.isBold) replacement = $"<b>{replacement}</b>";
                        return replacement;
                    }
                    return key;
                });
            }
            return result;
        }

        private static bool IsLikelyKaomoji(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            string content = input.Substring(1, input.Length - 2);
            if (Regex.IsMatch(content, @"[\u4e00-\u9fa5]")) return false;
            char[] kaomojiSymbols = new char[] { '_', '^', '=', ';', '@', 'o', '0', 'O' };
            if (content.IndexOfAny(kaomojiSymbols) >= 0) return true;
            return false;
        }

        private static string GenerateRainbowText(string input)
        {
            StringBuilder sb = new StringBuilder();
            float timeOffset = Time.realtimeSinceStartup * 2f;
            int cleanCharIndex = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '<') { int closeIndex = input.IndexOf('>', i); if (closeIndex != -1) { sb.Append(input.Substring(i, closeIndex - i + 1)); i = closeIndex; continue; } }
                if (char.IsWhiteSpace(c)) { sb.Append(c); continue; }
                float hue = (cleanCharIndex * 0.1f - timeOffset) % 1f; if (hue < 0) hue += 1f;
                Color rainbowColor = Color.HSVToRGB(hue, 0.8f, 1f);
                string hex = ColorUtility.ToHtmlStringRGB(rainbowColor);
                sb.Append($"<color=#{hex}>{c}</color>"); cleanCharIndex++;
            }
            return sb.ToString();
        }

        private static string GenerateBlackWhiteText(string input)
        {
            StringBuilder sb = new StringBuilder();
            int cleanCharIndex = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '<') { int closeIndex = input.IndexOf('>', i); if (closeIndex != -1) { sb.Append(input.Substring(i, closeIndex - i + 1)); i = closeIndex; continue; } }
                if (char.IsWhiteSpace(c)) { sb.Append(c); continue; }
                if (cleanCharIndex % 2 == 0) sb.Append($"<b><color=#FFFFFF>{c}</color></b>"); else sb.Append($"<color=#666666>{c}</color>"); cleanCharIndex++;
            }
            return sb.ToString();
        }

        // =================================================================
        // 公共 UI 绘制方法 (静态)
        // =================================================================
        public static void DrawHistoryArea(Listing_Standard listing, float width, float viewHeightParam = 300f)
        {
            listing.Label("<b>" + "RTDC_ChatHistoryViewer".Translate() + "</b>");
            listing.Label("RTDC_HistoryInfo".Translate(), -1f, null);

            // 日志区域
            Rect viewerRect = listing.GetRect(viewHeightParam);
            Widgets.DrawBoxSolid(viewerRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Widgets.DrawBox(viewerRect);

            float totalContentHeight = 0f;
            float viewWidth = viewerRect.width - 20f;
            for (int i = 0; i < SessionHistory.Count; i++)
            {
                var item = SessionHistory[i];
                string contentToShow = item.CleanContent;
                Vector2 nameSize = Text.CalcSize(item.PawnName);
                float textWidth = viewWidth - (nameSize.x + 15f);
                float textHeight = Text.CalcHeight(contentToShow, textWidth);
                totalContentHeight += Mathf.Max(24f, textHeight);
            }

            Rect contentRect = new Rect(0, 0, viewWidth, totalContentHeight);
            Widgets.BeginScrollView(viewerRect, ref logViewerScrollPos, contentRect);
            float curY = 0f;

            for (int i = 0; i < SessionHistory.Count; i++)
            {
                var item = SessionHistory[i];
                string contentToShow = item.CleanContent;
                Vector2 nameSize = Text.CalcSize(item.PawnName);
                float textWidth = viewWidth - (nameSize.x + 15f);
                float textHeight = Text.CalcHeight(contentToShow, textWidth);
                float rowHeight = Mathf.Max(24f, textHeight);

                Rect lineRect = new Rect(5f, curY, viewWidth, rowHeight);
                Rect nameRect = new Rect(lineRect.x, lineRect.y, nameSize.x + 10f, rowHeight);

                GUI.color = item.NameColor;
                Widgets.Label(nameRect, $"[{item.PawnName}]");

                GUI.color = Color.white;
                Rect diagRect = new Rect(nameRect.xMax, lineRect.y, textWidth, rowHeight);
                Widgets.Label(diagRect, contentToShow);
                curY += rowHeight;
            }
            Widgets.EndScrollView();

            listing.Gap(10f);

            // 工具区域
            listing.Label("<b>" + "RTDC_ExportPathSetting".Translate() + "</b>");

            string currentPath = string.IsNullOrEmpty(settings.customExportPath) ? GenFilePaths.SaveDataFolderPath : settings.customExportPath;

            Rect btnRowRect = listing.GetRect(30f);
            float btnWidth = (btnRowRect.width - 10f) / 2f;

            if (Widgets.ButtonText(new Rect(btnRowRect.x, btnRowRect.y, btnWidth, 30f), "RTDC_SetPath".Translate()))
            {
                Find.WindowStack.Add(new Dialog_Input("RTDC_SetPath".Translate(), "请粘贴或输入路径", currentPath, (val) => {
                    if (Directory.Exists(val)) settings.customExportPath = val;
                    else Messages.Message("RTDC_PathInvalid".Translate(), MessageTypeDefOf.RejectInput, false);
                }));
            }

            if (Widgets.ButtonText(new Rect(btnRowRect.x + btnWidth + 10f, btnRowRect.y, btnWidth, 30f), "RTDC_OpenFolder".Translate()))
            {
                if (Directory.Exists(currentPath)) Application.OpenURL(currentPath);
                else Messages.Message("RTDC_PathInvalid".Translate(), MessageTypeDefOf.RejectInput, false);
            }

            listing.Gap(5f);
            Rect textRect = listing.GetRect(24f);
            GUI.color = Color.gray;
            Widgets.Label(textRect, "RTDC_CurrentPathLabel".Translate() + " " + currentPath);
            GUI.color = Color.white;

            if (listing.ButtonText("RTDC_ResetPath".Translate()))
            {
                settings.customExportPath = "";
            }

            listing.Gap(10f);
            listing.Label("<b>" + "RTDC_LogTools".Translate() + "</b>");
            Rect copyBtnRect2 = listing.GetRect(30f);
            float quarterWidth = width / 4f - 5f;
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x, copyBtnRect2.y, quarterWidth, 30f), "RTDC_CopyPlain".Translate())) CopyChatLogToClipboard(false);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + quarterWidth + 5f, copyBtnRect2.y, quarterWidth, 30f), "RTDC_CopyRich".Translate())) CopyChatLogToClipboard(true);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + (quarterWidth + 5f) * 2, copyBtnRect2.y, quarterWidth, 30f), "RTDC_ExportTxt".Translate())) ExportChatLog(false);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + (quarterWidth + 5f) * 3, copyBtnRect2.y, quarterWidth, 30f), "RTDC_ExportHtml".Translate())) ExportChatLog(true);
        }

        public static void ApplyTabVisibility()
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamed("RTDC_HistoryTab", false);
            if (def != null)
            {
                def.buttonVisible = settings.showHistoryTab;
            }
        }

        // ==========================================
        // UI 方法 (设置界面)
        // ==========================================
        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (settings == null) settings = GetSettings<DynamicColorSettings>();

            Rect viewRect = new Rect(0, 0, inRect.width - 16f, 1300f);
            Widgets.BeginScrollView(inRect, ref mainScrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            Text.Font = GameFont.Medium;
            listing.Label("RTDC_SettingsTitle".Translate());
            Text.Font = GameFont.Small;
            listing.GapLine();

            // 基础设置
            listing.Label("<b>" + "RTDC_BasicSettings".Translate() + "</b>");
            listing.CheckboxLabeled("RTDC_EnableMod".Translate(), ref settings.isGlobalEnabled, "RTDC_EnableModDesc".Translate());

            bool oldShowTab = settings.showHistoryTab;
            listing.CheckboxLabeled("RTDC_ShowHistoryTab".Translate(), ref settings.showHistoryTab, "RTDC_ShowHistoryTabDesc".Translate());
            if (oldShowTab != settings.showHistoryTab)
            {
                ApplyTabVisibility();
            }

            listing.CheckboxLabeled("RTDC_EnableRimTalkNameColoring".Translate(), ref settings.enableRimTalkNameColoring, "RTDC_EnableRimTalkNameColoringDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableChatColoring".Translate(), ref settings.enableChatColoring, "RTDC_EnableChatColoringDesc".Translate());

            if (hasBubblesMod) listing.CheckboxLabeled("RTDC_EnableBubblesSync".Translate(), ref settings.enableBubblesSync, "RTDC_EnableBubblesSyncDesc".Translate());
            else { GUI.color = Color.gray; if (Widgets.ButtonText(listing.GetRect(30f), "RTDC_BubblesNotDetected".Translate() + " (点击重试)")) TryPatchInteractionBubbles(); GUI.color = Color.white; }

            listing.GapLine();
            listing.Label("<b>" + "RTDC_HistorySettings".Translate() + "</b>");
            listing.CheckboxLabeled("RTDC_AutoExportHistory".Translate(), ref settings.autoExportHistory, "RTDC_AutoExportHistoryDesc".Translate());

            if (listing.ButtonText("RTDC_OpenHistoryWindow".Translate(), "RTDC_OpenHistoryWindowDesc".Translate()))
            {
                Window existing = Find.WindowStack.WindowOfType<RimTalkHistoryWindow>();
                if (existing != null) existing.Close();
                else Find.WindowStack.Add(new RimTalkHistoryWindow());
            }

            listing.GapLine();
            listing.Label("<b>" + "RTDC_ColorRules".Translate() + "</b>");
            listing.CheckboxLabeled("RTDC_EnableForPrisoners".Translate(), ref settings.enableForPrisoners, "RTDC_EnableForPrisonersDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableForSlaves".Translate(), ref settings.enableForSlaves, "RTDC_EnableForSlavesDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableForGuests".Translate(), ref settings.enableForGuests, "RTDC_EnableForGuestsDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableForFriendlies".Translate(), ref settings.enableForFriendlies, "RTDC_EnableForFriendliesDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableForEnemies".Translate(), ref settings.enableForEnemies, "RTDC_EnableForEnemiesDesc".Translate());

            // [新增] UI: 非人类设置
            listing.Gap(5f);
            listing.CheckboxLabeled("RTDC_EnableForNonHumans".Translate(), ref settings.enableForNonHumans, "RTDC_EnableForNonHumansDesc".Translate());
            if (settings.enableForNonHumans)
            {
                Rect nhRect = listing.GetRect(24f);
                float nhBtnW = 180f; // [变量名冲突修复] btnW -> nhBtnW
                if (Widgets.ButtonText(new Rect(nhRect.x, nhRect.y, nhBtnW, 24f), "RTDC_SelectNonHumanColor".Translate()))
                {
                    Find.WindowStack.Add(new ColorPickerWindow(settings.nonHumanDefaultColor, "Non-Human", (c) => settings.nonHumanDefaultColor = c));
                }
                Widgets.DrawBoxSolid(new Rect(nhRect.x + nhBtnW + 10f, nhRect.y, 24f, 24f), settings.nonHumanDefaultColor);
            }

            listing.Gap(5f);
            listing.CheckboxLabeled("RTDC_UseFactionColor".Translate(), ref settings.useFactionColor, "RTDC_UseFactionColorDesc".Translate());
            bool oldAuto = settings.autoApplyFavColor;
            listing.CheckboxLabeled("RTDC_AutoFavColor".Translate(), ref settings.autoApplyFavColor, "RTDC_AutoFavColorDesc".Translate());
            if (settings.autoApplyFavColor) listing.CheckboxLabeled("  ↳ " + "RTDC_AutoApplyBold".Translate(), ref settings.autoApplyBold, "RTDC_AutoApplyBoldDesc".Translate());
            if (oldAuto != settings.autoApplyFavColor) settings.RebuildCache();

            listing.Gap(10f);
            listing.Label("<b>" + "RTDC_StyleSettings".Translate() + "</b>");
            listing.CheckboxLabeled("RTDC_EnableSelfTalkColor".Translate(), ref settings.enableSelfTalkColor, "RTDC_EnableSelfTalkColorDesc".Translate());

            if (settings.enableSelfTalkColor)
            {
                Rect stRect = listing.GetRect(30f);
                if (Widgets.ButtonText(new Rect(stRect.x, stRect.y, 150f, 30f), "RTDC_SelectSelfTalkColor".Translate()))
                {
                    Find.WindowStack.Add(new ColorPickerWindow(settings.selfTalkColor, "(* Thinking *)", (newColor) => {
                        settings.selfTalkColor = newColor;
                    }));
                }
                Widgets.DrawBoxSolid(new Rect(stRect.x + 160f, stRect.y, 30f, 30f), settings.selfTalkColor);

                listing.Gap(5f);

                Rect stStyleRect = listing.GetRect(24f);
                Vector2 boldSize = Text.CalcSize("RTDC_Bold".Translate());
                Vector2 italicSize = Text.CalcSize("RTDC_SelfTalkItalic".Translate());

                Widgets.CheckboxLabeled(new Rect(stStyleRect.x, stStyleRect.y, boldSize.x + 30f, 24f), "RTDC_Bold".Translate(), ref settings.selfTalkBold);
                Widgets.CheckboxLabeled(new Rect(stStyleRect.x + boldSize.x + 40f, stStyleRect.y, italicSize.x + 30f, 24f), "RTDC_SelfTalkItalic".Translate(), ref settings.selfTalkItalic);
            }

            listing.CheckboxLabeled("RTDC_EnableRainbow".Translate(), ref settings.enableRainbowEffect, "RTDC_EnableRainbowDesc".Translate());
            listing.CheckboxLabeled("RTDC_EnableBW".Translate(), ref settings.enableBlackWhiteEffect, "RTDC_EnableBWDesc".Translate());

            listing.Gap(10f);
            listing.Label("<b>" + "RTDC_DataManage".Translate() + "</b>");
            listing.Label("1. " + "RTDC_ManageNames".Translate(), -1f, "RTDC_ManageNamesDesc".Translate());
            Rect nameCtrlRect = listing.GetRect(30f);
            inputNameBuffer = Widgets.TextField(new Rect(nameCtrlRect.x, nameCtrlRect.y, 120f, 30f), inputNameBuffer ?? "");
            if (Widgets.ButtonText(new Rect(nameCtrlRect.x + 130f, nameCtrlRect.y, 80f, 30f), "RTDC_Add".Translate())) { AddNewEntry(settings.nameEntries, inputNameBuffer); inputNameBuffer = ""; }
            float btnW = 100f; float rightEdge = viewRect.width - 20f;
            if (Widgets.ButtonText(new Rect(rightEdge - btnW * 2 - 10f, nameCtrlRect.y, btnW, 30f), "RTDC_ImportColonists".Translate())) { ImportAllPawns(); }
            if (Widgets.ButtonText(new Rect(rightEdge - btnW, nameCtrlRect.y, btnW, 30f), "RTDC_ClearNames".Translate())) { settings.nameEntries.Clear(); settings.RebuildCache(); Messages.Message("RTDC_Cleared".Translate(), MessageTypeDefOf.TaskCompletion, false); }
            listing.Gap(5f);
            DrawCustomList(listing, settings.nameEntries, "Name");
            listing.Gap(10f);
            listing.Label("2. " + "RTDC_ManageKeywords".Translate(), -1f, "RTDC_ManageKeywordsDesc".Translate());
            Rect keyCtrlRect = listing.GetRect(30f);
            inputKeywordBuffer = Widgets.TextField(new Rect(keyCtrlRect.x, keyCtrlRect.y, 120f, 30f), inputKeywordBuffer ?? "");
            if (Widgets.ButtonText(new Rect(keyCtrlRect.x + 130f, keyCtrlRect.y, 80f, 30f), "RTDC_Add".Translate())) { AddNewEntry(settings.keywordEntries, inputKeywordBuffer); inputKeywordBuffer = ""; }
            listing.Gap(5f);
            DrawCustomList(listing, settings.keywordEntries, "Keyword");
            listing.Gap(20f);
            if (listing.ButtonText("RTDC_ClearAll".Translate())) { settings.nameEntries.Clear(); settings.keywordEntries.Clear(); settings.RebuildCache(); Messages.Message("RTDC_Cleared".Translate(), MessageTypeDefOf.TaskCompletion, false); }

            listing.End();
            Widgets.EndScrollView();
        }

        private static void DrawCustomList(Listing_Standard listing, List<ColorEntry> entries, string typeLabel)
        {
            if (entries == null) return;
            List<ColorEntry> toRemove = new List<ColorEntry>();
            bool changed = false;

            for (int i = 0; i < entries.Count; i++)
            {
                ColorEntry entry = entries[i];
                if (entry == null) continue;
                Rect rect = listing.GetRect(28f);

                string newText = Widgets.TextField(new Rect(rect.x, rect.y, rect.width * 0.4f, 24f), entry.text);
                if (newText != entry.text) { entry.text = newText; changed = true; }

                Rect colorRect = new Rect(rect.x + rect.width * 0.42f, rect.y, rect.width * 0.3f, 24f);

                if (Widgets.ButtonText(colorRect, "RTDC_SelectColor".Translate()))
                {
                    Find.WindowStack.Add(new ColorPickerWindow(entry.color, entry.text, (newColor) => {
                        entry.color = newColor;
                        settings.RebuildCache();
                    }));
                }
                Widgets.DrawBoxSolid(new Rect(colorRect.x + colorRect.width - 24f, colorRect.y, 24f, 24f), entry.color);

                Rect boldRect = new Rect(rect.x + rect.width * 0.74f, rect.y, 24f, 24f);
                bool oldBold = entry.isBold;
                Widgets.Checkbox(boldRect.position, ref entry.isBold);
                if (oldBold != entry.isBold) changed = true;
                TooltipHandler.TipRegion(boldRect, "RTDC_Bold".Translate());

                if (Widgets.ButtonText(new Rect(rect.x + rect.width - 60f, rect.y, 60f, 24f), "RTDC_Delete".Translate())) { toRemove.Add(entry); changed = true; }
            }
            foreach (var item in toRemove) entries.Remove(item);
            if (changed || toRemove.Count > 0) settings.RebuildCache();
        }

        public static void AddNewEntry(List<ColorEntry> list, string text)
        {
            if (string.IsNullOrEmpty(text) || list == null) return;
            foreach (var e in list) if (e != null && e.text == text) return;
            list.Add(new ColorEntry(text, Color.white));
            settings.RebuildCache();
        }

        public static void ImportAllPawns()
        {
            if (Current.Game == null || Current.Game.Maps == null) return;
            if (settings.nameEntries == null) settings.nameEntries = new List<ColorEntry>();
            List<Pawn> pawns = new List<Pawn>();
            foreach (Map map in Current.Game.Maps) { pawns.AddRange(map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike)); }
            int count = 0;
            foreach (var p in pawns)
            {
                if (p == null || p.Name == null) continue;
                string name = p.Name.ToStringShort;
                bool exists = false;
                foreach (var e in settings.nameEntries) if (e != null && e.text == name) exists = true;
                if (!exists) { settings.nameEntries.Add(new ColorEntry(name, Color.white)); count++; }
            }
            settings.RebuildCache();
            Messages.Message($"RTDC_ImportDone".Translate() + $" ({count})", MessageTypeDefOf.PositiveEvent, false);
        }

        public static void CopyChatLogToClipboard(bool richText)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("RTDC_ExportHeader".Translate());
            sb.AppendLine(new string('-', 30));
            foreach (var msg in SessionHistory)
            {
                if (richText) sb.AppendLine($"<b>{msg.PawnName}:</b> {msg.Content}");
                else sb.AppendLine($"{msg.PawnName}: {msg.OriginalContent}");
            }
            GUIUtility.systemCopyBuffer = sb.ToString();
            Messages.Message("RTDC_Copied".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        public static void ExportChatLog(bool toHtml, bool silent = false)
        {
            if (SessionHistory.Count == 0)
            {
                if (!silent) Messages.Message("RTDC_NoLogFound".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            try
            {
                if (!silent)
                {
                    Find.TickManager.Pause();
                }

                string timestamp = DateTime.Now.ToString("MM-dd_HH-mm-ss");
                string ext = toHtml ? "html" : "txt";
                string filename = $"RTDC Auto-{timestamp}.{ext}";

                string basePath = string.IsNullOrEmpty(settings.customExportPath) ? GenFilePaths.SaveDataFolderPath : settings.customExportPath;
                if (!Directory.Exists(basePath)) Directory.CreateDirectory(basePath);

                string path = Path.Combine(basePath, filename);
                StringBuilder sb = new StringBuilder();
                string headerTitle = "RTDC_ExportHeader".Translate();

                if (toHtml)
                {
                    sb.AppendLine("<html><head><meta charset='utf-8'><style>body { background-color: #222; color: #eee; font-family: sans-serif; } .msg { margin-bottom: 5px; } .name { font-weight: bold; } </style></head><body>");
                    sb.AppendLine($"<h2>{headerTitle} - {DateTime.Now}</h2>");
                    foreach (var msg in SessionHistory)
                    {
                        string nameHex = ColorUtility.ToHtmlStringRGB(msg.NameColor);
                        string nameStyle = $"color: #{nameHex};";
                        if (msg.NameBold) nameStyle += " font-weight: bold;";
                        string contentHtml = msg.Content.Replace("\n", "<br/>").Replace("<b>", "<strong>").Replace("</b>", "</strong>").Replace("<i>", "<em>").Replace("</i>", "</em>");
                        contentHtml = Regex.Replace(contentHtml, @"<color=#([0-9A-Fa-f]{6})>(.*?)</color>", "<span style='color:#$1'>$2</span>");
                        sb.AppendLine($"<div class='msg'><span style='{nameStyle}'>[{msg.PawnName}]</span>: {contentHtml}</div>");
                    }
                    sb.AppendLine("</body></html>");
                }
                else
                {
                    sb.AppendLine($"{headerTitle} - {DateTime.Now}");
                    sb.AppendLine(new string('-', 30));
                    foreach (var msg in SessionHistory) { sb.AppendLine($"[{msg.PawnName}]: {msg.OriginalContent}"); }
                }

                File.WriteAllText(path, sb.ToString());
                if (silent)
                {
                    Messages.Message("RTDC_AutoExportMsg".Translate(filename), MessageTypeDefOf.PositiveEvent, false);
                }
                else
                {
                    Messages.Message("RTDC_ExportSuccess".Translate(filename), MessageTypeDefOf.TaskCompletion, false);
                    Application.OpenURL(basePath);
                }
            }
            catch (Exception ex) { Log.Error($"[RimTalk DynamicColors] Export failed: {ex}"); if (!silent) Messages.Message("Error exporting log", MessageTypeDefOf.RejectInput, false); }
        }
    }

    // =======================================================
    // 底部菜单按钮逻辑 (MainTab)
    // =======================================================
    public class MainButtonWorker_RimTalkHistory : MainButtonWorker
    {
        public override void Activate()
        {
            Window existing = Find.WindowStack.WindowOfType<RimTalkHistoryWindow>();
            if (existing != null) existing.Close();
            else Find.WindowStack.Add(new RimTalkHistoryWindow());
        }

        public override void DoButton(Rect rect)
        {
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(rect))
            {
                Find.WindowStack.Add(new Dialog_ModSettings(DynamicColorMod.settings.Mod));
                Event.current.Use();
                return;
            }
            base.DoButton(rect);
        }
    }

    // =======================================================
    // 独立的历史记录窗口 (保持游戏进度，大尺寸)
    // =======================================================
    public class RimTalkHistoryWindow : Window
    {
        public RimTalkHistoryWindow()
        {
            this.doCloseX = true;
            this.forcePause = false;
            this.preventCameraMotion = false;
            this.draggable = true;
            this.resizeable = true;
            this.optionalTitle = "RTDC_HistoryTab".Translate();
        }

        public override Vector2 InitialSize => new Vector2(600f, 800f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            float logViewHeight = inRect.height - 280f;
            if (logViewHeight < 200f) logViewHeight = 200f;

            DynamicColorMod.DrawHistoryArea(listing, inRect.width, logViewHeight);

            listing.End();
        }
    }

    // [新增] 简单的输入对话框类
    public class Dialog_Input : Window
    {
        private string header;
        private string text;
        private string placeholder;
        private Action<string> onConfirm;

        public Dialog_Input(string header, string placeholder, string initialText, Action<string> onConfirm)
        {
            this.header = header;
            this.placeholder = placeholder;
            this.text = initialText;
            this.onConfirm = onConfirm;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 200f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            l.Label(header);
            l.Gap();
            text = l.TextEntry(text);
            l.Gap();
            if (l.ButtonText("OK"))
            {
                onConfirm?.Invoke(text);
                Close();
            }
            l.End();
        }
    }

    // =======================================================
    // 升级版颜色选择窗口
    // =======================================================
    public class ColorPickerWindow : Window
    {
        private Color _tempColor;
        private Color _originalColor;
        private string _previewText;
        private Action<Color> _onCommit;
        private static Texture2D _colorWheelTex;

        public ColorPickerWindow(Color initial, string text, Action<Color> onCommit)
        {
            this._tempColor = initial;
            this._originalColor = initial;
            this._previewText = string.IsNullOrEmpty(text) ? "Preview" : text;
            this._onCommit = onCommit;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(400f, 650f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            Text.Font = GameFont.Medium;
            listing.Label("RTDC_SelectColor".Translate());
            Text.Font = GameFont.Small;
            listing.Gap();

            Rect wheelRect = listing.GetRect(200f);
            Rect centeredWheel = new Rect(wheelRect.x + (wheelRect.width - 180f) / 2f, wheelRect.y, 180f, 180f);
            DrawColorWheel(centeredWheel, ref _tempColor);
            listing.Gap(20f);

            DrawRGBSlider(listing, ref _tempColor.r, "R", Color.red);
            DrawRGBSlider(listing, ref _tempColor.g, "G", Color.green);
            DrawRGBSlider(listing, ref _tempColor.b, "B", Color.blue);
            DrawRGBSlider(listing, ref _tempColor.a, "RTDC_Alpha".Translate(), Color.gray);

            listing.Gap(20f);

            listing.Label("RTDC_Preview".Translate() + ":");
            Rect previewRect = listing.GetRect(60f);
            Widgets.DrawBoxSolid(previewRect, new Color(0.1f, 0.1f, 0.1f, 1f));
            Widgets.DrawBox(previewRect);

            Color oldColor = GUI.color;
            GUI.color = _tempColor;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(previewRect, _previewText);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            GUI.color = oldColor;

            listing.Gap(20f);

            Rect btnRect = listing.GetRect(30f);
            float btnW = btnRect.width / 2f - 5f;

            if (Widgets.ButtonText(new Rect(btnRect.x, btnRect.y, btnW, 30f), "OK".Translate()))
            {
                _onCommit?.Invoke(_tempColor);
                this.Close();
            }

            if (Widgets.ButtonText(new Rect(btnRect.x + btnW + 10f, btnRect.y, btnW, 30f), "RTDC_Reset".Translate()))
            {
                _tempColor = _originalColor;
            }

            listing.End();
        }

        private void DrawRGBSlider(Listing_Standard listing, ref float colorComponent, string label, Color guideColor)
        {
            Rect rect = listing.GetRect(30f);
            Rect labelRect = new Rect(rect.x, rect.y, 100f, 30f);
            GUI.color = guideColor;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;

            Rect sliderRect = new Rect(rect.x + 80f, rect.y + 5f, 200f, 20f);
            colorComponent = GUI.HorizontalSlider(sliderRect, colorComponent, 0f, 1f);

            Rect inputRect = new Rect(rect.x + 290f, rect.y, 60f, 30f);
            int val = Mathf.RoundToInt(colorComponent * 255f);
            string buffer = val.ToString();
            Widgets.TextFieldNumeric(inputRect, ref val, ref buffer, 0f, 255f);
            colorComponent = val / 255f;
            listing.Gap(2f);
        }

        private void DrawColorWheel(Rect rect, ref Color currentColor)
        {
            if (_colorWheelTex == null || _colorWheelTex.width != (int)rect.width)
                _colorWheelTex = GenerateColorWheelTexture((int)rect.width);
            GUI.DrawTexture(rect, _colorWheelTex);
            if (Mouse.IsOver(rect) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag))
            {
                Vector2 mousePos = Event.current.mousePosition - rect.position;
                Vector2 center = new Vector2(rect.width / 2f, rect.height / 2f);
                float radius = rect.width / 2f;
                float dist = Vector2.Distance(mousePos, center);
                if (dist <= radius)
                {
                    float angle = Mathf.Atan2(mousePos.y - center.y, mousePos.x - center.x) * Mathf.Rad2Deg;
                    if (angle < 0) angle += 360f;
                    float hue = angle / 360f;
                    float sat = dist / radius;
                    float currentAlpha = currentColor.a;
                    currentColor = Color.HSVToRGB(hue, sat, 1f);
                    currentColor.a = currentAlpha;
                    Event.current.Use();
                }
            }
        }

        private static Texture2D GenerateColorWheelTexture(int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 current = new Vector2(x, y);
                    float dist = Vector2.Distance(current, center);
                    if (dist > radius) pixels[y * size + x] = Color.clear;
                    else
                    {
                        float angle = Mathf.Atan2(current.y - center.y, current.x - center.x) * Mathf.Rad2Deg;
                        if (angle < 0) angle += 360f;
                        pixels[y * size + x] = Color.HSVToRGB(angle / 360f, dist / radius, 1f);
                    }
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }

    // =======================================================
    // 4. Harmony 补丁类
    // =======================================================
    [HarmonyPatch(typeof(PawnNameColorUtility), "PawnNameColorOf")]
    public static class Patch_PawnNameColorOf_ContextAware
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref Color __result)
        {
            if (DynamicColorMod.settings == null || !DynamicColorMod.settings.isGlobalEnabled || pawn == null) return true;

            // 只在聊天气泡或聊天记录时染色
            if (!DynamicColorMod.IsDrawingBubble && !DynamicColorMod.IsDrawingChatLog) return true;

            // --- 资格检查 ---
            bool allow = false;
            if (pawn.RaceProps.Humanlike)
            {
                if (pawn.IsPrisoner) allow = DynamicColorMod.settings.enableForPrisoners;
                else if (pawn.IsSlave) allow = DynamicColorMod.settings.enableForSlaves;
                else if (pawn.HostileTo(Faction.OfPlayer)) allow = DynamicColorMod.settings.enableForEnemies;
                else if (pawn.Faction != null && !pawn.Faction.IsPlayer) allow = DynamicColorMod.settings.enableForFriendlies;
                else if (pawn.Faction == null) allow = DynamicColorMod.settings.enableForGuests;
                else allow = true; // 玩家殖民者
            }
            else
            {
                // 动物/机械体
                allow = DynamicColorMod.settings.enableForNonHumans;
            }

            if (!allow) return true;

            // --- 颜色获取 ---
            // 1. 自定义名字
            if (DynamicColorMod.settings.GetCombinedCache().TryGetValue(pawn.Name.ToStringShort, out StyleData style))
            {
                __result = style.color;
                return false;
            }

            // 2. 喜爱颜色 (仅限人类)
            if (DynamicColorMod.settings.autoApplyFavColor && pawn.RaceProps.Humanlike && pawn.story != null && pawn.story.favoriteColor != null)
            {
                __result = pawn.story.favoriteColor.color;
                return false;
            }

            // 3. 派系颜色
            if (DynamicColorMod.settings.useFactionColor && pawn.Faction != null)
            {
                __result = pawn.Faction.Color;
                return false;
            }

            // 4. 非人类默认颜色 (如果没派系或不显示派系颜色)
            if (!pawn.RaceProps.Humanlike && DynamicColorMod.settings.enableForNonHumans)
            {
                __result = DynamicColorMod.settings.nonHumanDefaultColor;
                return false;
            }

            return true;
        }
    }

    public static class Patch_DrawMessageLog_Manual
    {
        private static FieldInfo _cachedMessagesField;
        private static FieldInfo _pawnNameField;
        private static FieldInfo _dialogueField;
        private static FieldInfo _pawnInstField;

        private const int MaxHistoryCount = 200;

        public static void Prefix(object __instance)
        {
            DynamicColorMod.IsDrawingChatLog = true;

            if (DynamicColorMod.settings.enableRimTalkNameColoring && __instance != null)
            {
                try
                {
                    if (_cachedMessagesField == null) _cachedMessagesField = AccessTools.Field(__instance.GetType(), "_cachedMessagesForLog");
                    if (_cachedMessagesField == null) return;

                    var list = _cachedMessagesField.GetValue(__instance) as System.Collections.IList;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var msg = list[i];
                            if (msg == null) continue;
                            Type t = msg.GetType();

                            if (_pawnNameField == null) _pawnNameField = AccessTools.Field(t, "PawnName") ?? AccessTools.Field(t, "pawnName");
                            if (_pawnNameField == null) continue;

                            string rawName = _pawnNameField.GetValue(msg) as string;
                            if (string.IsNullOrEmpty(rawName) || rawName.StartsWith("<color=")) continue;

                            if (_pawnInstField == null) _pawnInstField = AccessTools.Field(t, "PawnInstance") ?? AccessTools.Field(t, "pawnInstance");
                            Pawn p = _pawnInstField?.GetValue(msg) as Pawn;
                            if (p == null) p = DynamicColorMod.FindPawnByName(rawName);

                            Color nameColor = Color.white;
                            bool foundColor = false;

                            // 这里调用同样的优先级逻辑
                            // 1. 缓存
                            if (DynamicColorMod.settings.GetCombinedCache().TryGetValue(rawName, out StyleData style))
                            {
                                nameColor = style.color;
                                foundColor = true;
                            }
                            // 2. 实时喜爱颜色 (人类)
                            else if (DynamicColorMod.settings.autoApplyFavColor && p != null && p.RaceProps.Humanlike && p.story != null && p.story.favoriteColor != null)
                            {
                                nameColor = p.story.favoriteColor.color;
                                foundColor = true;
                            }
                            // 3. 派系颜色
                            else if (DynamicColorMod.settings.useFactionColor && p != null && p.Faction != null)
                            {
                                nameColor = p.Faction.Color;
                                foundColor = true;
                            }
                            // 4. 非人类默认色
                            else if (p != null && !p.RaceProps.Humanlike && DynamicColorMod.settings.enableForNonHumans)
                            {
                                nameColor = DynamicColorMod.settings.nonHumanDefaultColor;
                                foundColor = true;
                            }
                            // 5. 资格兜底检查 (如果前面没命中，或者命中了但被开关ban了)
                            else if (p != null)
                            {
                                bool allow = false;
                                if (p.RaceProps.Humanlike)
                                {
                                    if (p.IsPrisoner) allow = DynamicColorMod.settings.enableForPrisoners;
                                    else if (p.IsSlave) allow = DynamicColorMod.settings.enableForSlaves;
                                    else if (p.HostileTo(Faction.OfPlayer)) { nameColor = Color.red; allow = DynamicColorMod.settings.enableForEnemies; foundColor = true; } // 敌人特殊红
                                }
                                if (!allow && !foundColor) foundColor = false;
                            }

                            if (foundColor)
                            {
                                string hex = ColorUtility.ToHtmlStringRGB(nameColor);
                                // [重要] 强制只染色，不加粗
                                string coloredName = $"<color=#{hex}>{rawName}</color>";
                                _pawnNameField.SetValue(msg, coloredName);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var methodLabel = AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new Type[] { typeof(Rect), typeof(string) });
            var methodColorize = AccessTools.Method(typeof(DynamicColorMod), nameof(DynamicColorMod.ColorizeString));
            foreach (var instruction in instructions) { if (instruction.Calls(methodLabel)) yield return new CodeInstruction(OpCodes.Call, methodColorize); yield return instruction; }
        }

        public static void Postfix(object __instance)
        {
            DynamicColorMod.IsDrawingChatLog = false;

            try
            {
                if (_cachedMessagesField == null) _cachedMessagesField = AccessTools.Field(__instance.GetType(), "_cachedMessagesForLog");
                if (_cachedMessagesField == null) return;

                var list = _cachedMessagesField.GetValue(__instance) as System.Collections.IList;
                if (list != null && list.Count > 0)
                {
                    int startIndex = -1;
                    var lastRecorded = DynamicColorMod.SessionHistory.LastOrDefault();

                    if (list.Count > 0)
                    {
                        var sampleMsg = list[0];
                        if (sampleMsg != null)
                        {
                            Type t = sampleMsg.GetType();
                            if (_pawnNameField == null) _pawnNameField = AccessTools.Field(t, "PawnName") ?? AccessTools.Field(t, "pawnName");
                            if (_dialogueField == null) _dialogueField = AccessTools.Field(t, "Dialogue") ?? AccessTools.Field(t, "dialogue");
                            if (_pawnInstField == null) _pawnInstField = AccessTools.Field(t, "PawnInstance") ?? AccessTools.Field(t, "pawnInstance");
                        }
                    }

                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var msg = list[i];
                        if (msg == null) continue;

                        string rawName = _pawnNameField?.GetValue(msg) as string ?? "Unknown";
                        rawName = Regex.Replace(rawName, @"<.*?>", "").Trim();
                        string rawDialogue = (_dialogueField?.GetValue(msg) as string ?? "").Trim();

                        if (lastRecorded != null &&
                            lastRecorded.PawnName == rawName &&
                            lastRecorded.OriginalContent.Trim() == rawDialogue)
                        {
                            startIndex = i;
                            break;
                        }
                    }

                    for (int i = startIndex + 1; i < list.Count; i++)
                    {
                        var msg = list[i];
                        if (msg == null) continue;

                        string rawName = _pawnNameField?.GetValue(msg) as string ?? "Unknown";
                        string cleanName = Regex.Replace(rawName, @"<.*?>", "").Trim();

                        string rawDialogue = _dialogueField?.GetValue(msg) as string ?? "";
                        Pawn p = _pawnInstField?.GetValue(msg) as Pawn;
                        if (p == null) p = DynamicColorMod.FindPawnByName(cleanName);

                        DynamicColorMod.CurrentProcessingPawn = p;
                        Color nameColor = Color.white;
                        bool nameBold = false;

                        // [日志内容染色逻辑修复] 必须与上面的逻辑一致
                        if (DynamicColorMod.settings.GetCombinedCache().TryGetValue(cleanName, out StyleData style))
                        {
                            nameColor = style.color;
                            nameBold = style.isBold;
                        }
                        else if (DynamicColorMod.settings.autoApplyFavColor && p != null && p.RaceProps.Humanlike && p.story != null && p.story.favoriteColor != null)
                        {
                            nameColor = p.story.favoriteColor.color;
                            nameBold = DynamicColorMod.settings.autoApplyBold;
                        }
                        else if (p != null && DynamicColorMod.settings.useFactionColor && p.Faction != null)
                        {
                            nameColor = p.Faction.Color;
                        }
                        else if (p != null && !p.RaceProps.Humanlike && DynamicColorMod.settings.enableForNonHumans)
                        {
                            nameColor = DynamicColorMod.settings.nonHumanDefaultColor;
                        }

                        string coloredDialogue = DynamicColorMod.ColorizeString(rawDialogue);
                        string cleanDialogue = DynamicColorMod.ColorizeStringInternal(rawDialogue, true);

                        LogItem newItem = new LogItem()
                        {
                            PawnName = cleanName,
                            Content = coloredDialogue,
                            CleanContent = cleanDialogue,
                            OriginalContent = rawDialogue,
                            NameColor = nameColor,
                            NameBold = nameBold,
                            Timestamp = DateTime.Now.ToShortTimeString(),
                            SourceObject = msg
                        };

                        DynamicColorMod.SessionHistory.Add(newItem);
                        DynamicColorMod.CurrentProcessingPawn = null;
                    }

                    if (DynamicColorMod.SessionHistory.Count >= MaxHistoryCount)
                    {
                        if (DynamicColorMod.settings.autoExportHistory)
                        {
                            DynamicColorMod.ExportChatLog(true, true);
                        }
                        DynamicColorMod.ClearSessionHistory();
                    }
                }
            }
            catch (Exception) { }
        }
    }

    public static class Patch_Bubbles_Bubble_Draw
    {
        public static void Prefix(object __instance) { DynamicColorMod.CurrentProcessingPawn = null; DynamicColorMod.CurrentRecipientPawn = null; DynamicColorMod.IsDrawingBubble = false; if (__instance == null) return; List<Pawn> found = new List<Pawn>(); foreach (FieldInfo field in DynamicColorMod.BubblePawnFields) { try { Pawn p = field.GetValue(__instance) as Pawn; if (p != null && !found.Contains(p)) found.Add(p); } catch { } } if (found.Count > 0) { DynamicColorMod.CurrentProcessingPawn = found[0]; if (found.Count > 1) DynamicColorMod.CurrentRecipientPawn = found[1]; } if (DynamicColorMod.CurrentProcessingPawn != null) { Pawn p = DynamicColorMod.CurrentProcessingPawn; bool enabled = true; if (p.IsPrisoner && !DynamicColorMod.settings.enableForPrisoners) enabled = false; else if (p.IsSlave && !DynamicColorMod.settings.enableForSlaves) enabled = false; else if (p.HostileTo(Faction.OfPlayer) && !DynamicColorMod.settings.enableForEnemies) enabled = false; else if (p.Faction != null && !p.Faction.IsPlayer && !p.HostileTo(Faction.OfPlayer) && !DynamicColorMod.settings.enableForFriendlies) enabled = false; if (!enabled) return; } DynamicColorMod.IsDrawingBubble = true; }
        public static void Postfix() { DynamicColorMod.IsDrawingBubble = false; DynamicColorMod.CurrentProcessingPawn = null; DynamicColorMod.CurrentRecipientPawn = null; }
    }
    public static class Patch_Bubbles_Bubble_GetText { public static void Postfix(ref string __result) { if (DynamicColorMod.IsDrawingBubble && DynamicColorMod.settings.enableBubblesSync && !string.IsNullOrEmpty(__result)) { __result = DynamicColorMod.ColorizeString(__result); } } }
    public static class Patch_CommonTranspiler { public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) { var methodLabel = AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new Type[] { typeof(Rect), typeof(string) }); var methodColorize = AccessTools.Method(typeof(DynamicColorMod), nameof(DynamicColorMod.ColorizeStringForBubbles)); foreach (var instruction in instructions) { if (instruction.Calls(methodLabel)) yield return new CodeInstruction(OpCodes.Call, methodColorize); yield return instruction; } } }

    public class IngestionOutcomeDoer_RemoveRimTalkEffects : IngestionOutcomeDoer { protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount) { if (pawn == null || pawn.health == null) return; bool removed = false; Hediff rainbow = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(DynamicColorMod.RainbowHediffDefName)); if (rainbow != null) { pawn.health.RemoveHediff(rainbow); removed = true; } Hediff blackWhite = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(DynamicColorMod.BlackWhiteHediffDefName)); if (blackWhite != null) { pawn.health.RemoveHediff(blackWhite); removed = true; } if (removed) Messages.Message($"{pawn.LabelShort} " + "RTDC_DrinkSoup".Translate(), pawn, MessageTypeDefOf.PositiveEvent); } }

    [HarmonyPatch(typeof(Game), "FinalizeInit")]
    public static class Patch_Game_FinalizeInit
    {
        static void Postfix()
        {
            DynamicColorMod.ClearSessionHistory();
            if (DynamicColorMod.settings != null)
                DynamicColorMod.settings.RebuildCache();
            DynamicColorMod.ApplyTabVisibility();
        }
    }
}