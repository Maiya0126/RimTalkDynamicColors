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
using Verse.Sound;

namespace RimTalkDynamicColors
{
    // ==========================================
    // v1.1.3 - 新增自定义符号样式
    // ==========================================

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

    public class BracketStyleEntry : IExposable
    {
        public string openSymbol = "";
        public string closeSymbol = "";
        public Color color = new Color(0.6f, 0.6f, 0.6f, 1f);
        public bool isBold = false;
        public bool isItalic = false;
        public BracketStyleEntry() { }
        public BracketStyleEntry(string open, string close, Color c, bool bold = false, bool italic = false) { openSymbol = open ?? ""; closeSymbol = close ?? ""; color = c; isBold = bold; isItalic = italic; }
        public void ExposeData() { Scribe_Values.Look(ref openSymbol, "openSymbol", ""); Scribe_Values.Look(ref closeSymbol, "closeSymbol", ""); Scribe_Values.Look(ref color, "color", new Color(0.6f, 0.6f, 0.6f, 1f)); Scribe_Values.Look(ref isBold, "isBold", false); Scribe_Values.Look(ref isItalic, "isItalic", false); }
    }

    public struct RainbowCacheEntry { public string cachedText; public float lastUpdateTime; }

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

        // [新增] 记录讲话的Pawn实体，用于绘制头像
        public Pawn SpeakerPawn;
    }

    // ==========================================
    // 2. 设置类
    // ==========================================
    public class DynamicColorSettings : ModSettings
    {
        public List<ColorEntry> nameEntries = new List<ColorEntry>();
        public List<ColorEntry> keywordEntries = new List<ColorEntry>();
        public List<BracketStyleEntry> bracketStyleEntries = new List<BracketStyleEntry>();

        public bool isGlobalEnabled = true;
        public bool showHistoryTab = true;

        // [新增] 是否在历史记录窗口显示头像
        public bool showAvatarInHistory = true;

        public bool enableRimTalkNameColoring = true;
        public bool enableChatColoring = true;
        public bool enableKeywordColoring = true;
        public bool enableBubblesSync = true;
        public bool autoExportHistory = false;
        public string customExportPath = "";

        public float historyWindowOpacity = 0.9f;

        public bool enableForPrisoners = true;
        public bool enableForSlaves = true;
        public bool enableForGuests = true;
        public bool enableForFriendlies = true;
        public bool enableForEnemies = true;

        public static readonly Color DefaultNonHumanColor = new Color(0.6f, 0.8f, 0.6f, 1f);
        public static readonly Color DefaultSelfTalkColor = new Color(0.6f, 0.6f, 0.6f, 1f);
        public static readonly Color DefaultKaomojiColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);

        public bool enableForNonHumans = false;
        public Color nonHumanDefaultColor = DefaultNonHumanColor;

        public bool useFactionColor = false;
        public bool autoApplyFavColor = true;
        public bool autoApplyBold = true;

        public bool enableSelfTalkColor = true;
        public bool selfTalkItalic = false;
        public bool selfTalkBold = false;
        public Color selfTalkColor = DefaultSelfTalkColor;

        public bool enableKaomojiColoring = true;
        public bool kaomojiBold = false;
        public Color kaomojiColor = DefaultKaomojiColor;

        public bool enableRainbowEffect = true;
        public bool enableBlackWhiteEffect = true;

        private Dictionary<string, StyleData> _combinedCache;
        private Regex _cachedRegexPattern;

        public bool IsManualNameEntry(string name) { if (nameEntries == null) return false; for (int i = 0; i < nameEntries.Count; i++) { if (nameEntries[i].text == name) return true; } return false; }
        public Dictionary<string, StyleData> GetCombinedCache() { if (_combinedCache == null) RebuildCache(); return _combinedCache; }
        public Regex GetCachedRegex() { if (_combinedCache == null) RebuildCache(); return _cachedRegexPattern; }

        public void RebuildCache()
        {
            DynamicColorMod.ClearCaches();
            _combinedCache = new Dictionary<string, StyleData>();

            if (nameEntries != null)
            {
                foreach (var entry in nameEntries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.text)) continue;
                    _combinedCache[entry.text] = new StyleData(entry.color, entry.isBold);
                }
            }
            else { nameEntries = new List<ColorEntry>(); }

            if (Current.ProgramState == ProgramState.Playing && Current.Game != null && Current.Game.Maps != null)
            {
                try
                {
                    List<Pawn> allPawns = new List<Pawn>();
                    foreach (var map in Current.Game.Maps)
                    {
                        allPawns.AddRange(map.mapPawns.AllPawnsSpawned.Where(p =>
                            p.RaceProps.Humanlike || (enableForNonHumans && (p.RaceProps.Animal || p.RaceProps.IsMechanoid))
                        ));
                    }

                    foreach (var p in allPawns)
                    {
                        if (p == null || p.Name == null) continue;
                        string pName = p.Name.ToStringShort;
                        if (string.IsNullOrEmpty(pName)) continue;
                        if (_combinedCache.ContainsKey(pName)) continue;

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
                        else { allow = enableForNonHumans; }

                        if (!allow) continue;

                        Color? finalColor = null;
                        bool finalBold = false;

                        if (p.RaceProps.Humanlike && autoApplyFavColor && p.story != null && p.story.favoriteColor != null)
                        {
                            finalColor = p.story.favoriteColor.color;
                            finalBold = autoApplyBold;
                        }
                        else if (useFactionColor && p.Faction != null)
                        {
                            finalColor = p.Faction.Color;
                            finalBold = false;
                        }
                        else if (!p.RaceProps.Humanlike && enableForNonHumans)
                        {
                            finalColor = nonHumanDefaultColor;
                            finalBold = false;
                        }

                        if (finalColor.HasValue) _combinedCache[pName] = new StyleData(finalColor.Value, finalBold);
                    }
                }
                catch (Exception) { }
            }

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
                try { _cachedRegexPattern = new Regex(pattern, RegexOptions.Compiled); } catch { _cachedRegexPattern = null; }
            }
            else { _cachedRegexPattern = null; }
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref nameEntries, "nameEntries", LookMode.Deep);
            Scribe_Collections.Look(ref keywordEntries, "keywordEntries", LookMode.Deep);
            Scribe_Collections.Look(ref bracketStyleEntries, "bracketStyleEntries", LookMode.Deep);
            if (nameEntries == null) nameEntries = new List<ColorEntry>();
            if (keywordEntries == null) keywordEntries = new List<ColorEntry>();
            if (bracketStyleEntries == null) bracketStyleEntries = new List<BracketStyleEntry>();

            Scribe_Values.Look(ref isGlobalEnabled, "isGlobalEnabled", true);
            Scribe_Values.Look(ref showHistoryTab, "showHistoryTab", true);
            Scribe_Values.Look(ref showAvatarInHistory, "showAvatarInHistory", true); // 保存头像开关
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
            Scribe_Values.Look(ref enableForNonHumans, "enableForNonHumans", false);
            Scribe_Values.Look(ref nonHumanDefaultColor, "nonHumanDefaultColor", DefaultNonHumanColor);

            Scribe_Values.Look(ref useFactionColor, "useFactionColor", false);
            Scribe_Values.Look(ref autoApplyFavColor, "autoApplyFavColor", true);
            Scribe_Values.Look(ref autoApplyBold, "autoApplyBold", true);

            Scribe_Values.Look(ref enableSelfTalkColor, "enableSelfTalkColor", true);
            Scribe_Values.Look(ref selfTalkItalic, "selfTalkItalic", false);
            Scribe_Values.Look(ref selfTalkBold, "selfTalkBold", false);
            Scribe_Values.Look(ref selfTalkColor, "selfTalkColor", DefaultSelfTalkColor);

            Scribe_Values.Look(ref enableKaomojiColoring, "enableKaomojiColoring", true);
            Scribe_Values.Look(ref kaomojiBold, "kaomojiBold", false);
            Scribe_Values.Look(ref kaomojiColor, "kaomojiColor", DefaultKaomojiColor);

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
        public static int lastBubbleFrame = -1;
        public static int lastChatLogFrame = -1;

        public const string RainbowHediffDefName = "RimTalk_RainbowStatus";
        public const string BlackWhiteHediffDefName = "RimTalk_BlackWhiteStatus";
        public static bool hasBubblesMod = false;

        private Vector2 mainScrollPosition = Vector2.zero;
        public static Vector2 logViewerScrollPos = Vector2.zero;

        private Vector2 nameListScrollPos = Vector2.zero;
        private Vector2 keywordListScrollPos = Vector2.zero;

        private string inputNameBuffer = "";
        private string inputKeywordBuffer = "";
        private string inputOpenSymbolBuffer = "";
        private string inputCloseSymbolBuffer = "";
        private Vector2 bracketListScrollPos = Vector2.zero;

        public static List<LogItem> SessionHistory = new List<LogItem>();
        public static HashSet<object> RecordedObjects = new HashSet<object>();
        public static HashSet<Guid> RecordedTalkIds = new HashSet<Guid>();
        public const int MaxHistoryCount = 200;

        public static List<FieldInfo> BubblePawnFields = new List<FieldInfo>();
        private static Dictionary<string, Pawn> _nameToPawnCache = new Dictionary<string, Pawn>();

        private static Dictionary<string, RainbowCacheEntry> _rainbowCache = new Dictionary<string, RainbowCacheEntry>();
        private const float RainbowCacheUpdateInterval = 0.05f;

        private static List<float> _cachedRowHeights = new List<float>();
        private static List<float> _cachedRowYPositions = new List<float>();
        private static float _cachedTotalHeight = 0f;
        private static int _lastHistoryCount = -1;
        private static string _lastFilterName = "";
        private static float _lastViewWidth = 0f;

        public static string filterPawnName = "All";
        public static bool filterDimMode = true;

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
                if (overlayType != null)
                {
                    MethodInfo targetMethod = AccessTools.Method(overlayType, "DrawMessageLog");
                    if (targetMethod != null)
                    {
                        harmony.Patch(targetMethod,
                            prefix: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Prefix)),
                            postfix: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Postfix)),
                            transpiler: new HarmonyMethod(typeof(Patch_DrawMessageLog_Manual), nameof(Patch_DrawMessageLog_Manual.Transpiler))
                        );
                    }
                }

                Type talkServiceType = AccessTools.TypeByName("RimTalk.Service.TalkService");
                if (talkServiceType != null)
                {
                    MethodInfo createInteractionMethod = AccessTools.Method(talkServiceType, "CreateInteraction");
                    if (createInteractionMethod != null)
                    {
                        harmony.Patch(createInteractionMethod,
                            postfix: new HarmonyMethod(typeof(Patch_TalkService_CreateInteraction), nameof(Patch_TalkService_CreateInteraction.Postfix)));
                    }
                }

                Type customDialogueType = AccessTools.TypeByName("RimTalk.Service.CustomDialogueService");
                if (customDialogueType != null)
                {
                    MethodInfo executeDialogueMethod = AccessTools.Method(customDialogueType, "ExecuteDialogue");
                    if (executeDialogueMethod != null)
                    {
                        harmony.Patch(executeDialogueMethod,
                            postfix: new HarmonyMethod(typeof(Patch_CustomDialogueService_ExecuteDialogue), nameof(Patch_CustomDialogueService_ExecuteDialogue.Postfix)));
                    }
                }
            }
            catch (Exception) { }
        }

        public static void ClearCaches() => _nameToPawnCache.Clear();

        public static void ClearSessionHistory()
        {
            SessionHistory.Clear();
            RecordedObjects.Clear();
            RecordedTalkIds.Clear();
        }

        public static void RecordToSessionHistory(Pawn pawn, string pawnName, string rawDialogue)
        {
            CurrentProcessingPawn = pawn;

            Color nameColor = Color.white;
            bool nameBold = false;

            if (settings.enableRimTalkNameColoring)
            {
                if (settings.IsManualNameEntry(pawnName) && settings.GetCombinedCache().TryGetValue(pawnName, out StyleData style))
                {
                    nameColor = style.color;
                    nameBold = style.isBold;
                }
                else if (settings.autoApplyFavColor && pawn != null && pawn.RaceProps.Humanlike && pawn.story != null && pawn.story.favoriteColor != null)
                {
                    nameColor = pawn.story.favoriteColor.color;
                    nameBold = settings.autoApplyBold;
                }
                else if (pawn != null && settings.useFactionColor && pawn.Faction != null)
                {
                    nameColor = pawn.Faction.Color;
                }
                else if (pawn != null && !pawn.RaceProps.Humanlike && settings.enableForNonHumans)
                {
                    nameColor = settings.nonHumanDefaultColor;
                }
                else if (settings.GetCombinedCache().TryGetValue(pawnName, out StyleData kwStyle))
                {
                    nameColor = kwStyle.color;
                    nameBold = kwStyle.isBold;
                }
            }

            string coloredDialogue = ColorizeString(rawDialogue);
            string cleanDialogue = ColorizeStringInternal(rawDialogue, true);

            SessionHistory.Add(new LogItem()
            {
                PawnName = pawnName,
                Content = coloredDialogue,
                CleanContent = cleanDialogue,
                OriginalContent = rawDialogue,
                NameColor = nameColor,
                NameBold = nameBold,
                Timestamp = DateTime.Now.ToShortTimeString(),
                SourceObject = null,
                SpeakerPawn = pawn
            });

            CurrentProcessingPawn = null;

            if (SessionHistory.Count >= MaxHistoryCount)
            {
                if (settings.autoExportHistory) ExportChatLog(true, true);
                ClearSessionHistory();
            }
        }

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
                    foreach (var field in AccessTools.GetDeclaredFields(targetType)) { if (field.FieldType == typeof(Pawn)) BubblePawnFields.Add(field); }
                    MethodInfo drawMethod = AccessTools.Method(targetType, "Draw");
                    MethodInfo getTextMethod = AccessTools.Method(targetType, "get_Text");
                    if (drawMethod != null)
                    {
                        harmony.Patch(drawMethod,
                            prefix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_Draw), nameof(Patch_Bubbles_Bubble_Draw.Prefix)),
                            postfix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_Draw), nameof(Patch_Bubbles_Bubble_Draw.Postfix)));
                        harmony.Patch(drawMethod, transpiler: new HarmonyMethod(typeof(Patch_CommonTranspiler), nameof(Patch_CommonTranspiler.Transpiler)));
                        hasBubblesMod = true;
                    }
                    if (getTextMethod != null) harmony.Patch(getTextMethod, postfix: new HarmonyMethod(typeof(Patch_Bubbles_Bubble_GetText), nameof(Patch_Bubbles_Bubble_GetText.Postfix)));
                }
            }
            catch (Exception) { }
        }

        private Type FindBubbleType()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) { if (asm.GetName().Name == "Bubbles") { Type t = asm.GetType("Bubbles.Bubble"); if (t != null) return t; foreach (var type in asm.GetTypes()) if (type.Name == "Bubble" && AccessTools.Method(type, "Draw") != null) return type; } }
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
            if (settings == null || !settings.isGlobalEnabled || !settings.enableChatColoring) return input;
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
                    if (settings.enableRainbowEffect && hediffSet.HasHediff(HediffDef.Named(RainbowHediffDefName))) return GenerateRainbowText(input);
                    if (settings.enableBlackWhiteEffect && hediffSet.HasHediff(HediffDef.Named(BlackWhiteHediffDefName))) return GenerateBlackWhiteText(input);
                }
            }

            string result = input;

            if (settings.enableSelfTalkColor)
            {
                result = ColorizeSelfTalkWithNesting(result);
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

        private static string ColorizeSelfTalkWithNesting(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            StringBuilder sb = new StringBuilder();
            int i = 0;

            while (i < input.Length)
            {
                char c = input[i];

                bool customMatched = false;
                if (settings.bracketStyleEntries != null)
                {
                    foreach (var entry in settings.bracketStyleEntries)
                    {
                        if (string.IsNullOrEmpty(entry.openSymbol) || string.IsNullOrEmpty(entry.closeSymbol)) continue;
                        if (c != entry.openSymbol[0]) continue;
                        if (i + entry.openSymbol.Length > input.Length) continue;
                        if (input.Substring(i, entry.openSymbol.Length) != entry.openSymbol) continue;

                        int searchStart = i + entry.openSymbol.Length;
                        int closeIdx = -1;
                        for (int j = searchStart; j <= input.Length - entry.closeSymbol.Length; j++)
                        {
                            if (input.Substring(j, entry.closeSymbol.Length) == entry.closeSymbol)
                            {
                                closeIdx = j;
                                break;
                            }
                        }

                        if (closeIdx != -1)
                        {
                            string fullMatch = input.Substring(i, closeIdx + entry.closeSymbol.Length - i);
                            string hex = ColorUtility.ToHtmlStringRGBA(entry.color);
                            string styledPart = $"<color=#{hex}>{fullMatch}</color>";
                            if (entry.isItalic) styledPart = $"<i>{styledPart}</i>";
                            if (entry.isBold) styledPart = $"<b>{styledPart}</b>";
                            sb.Append(styledPart);
                            i = closeIdx + entry.closeSymbol.Length;
                            customMatched = true;
                            break;
                        }
                    }
                }
                if (customMatched) continue;

                if (c == '(' || c == '（' || c == '【' || c == '*')
                {
                    int endIndex = -1;

                    if (c == '*')
                    {
                        int nextStar = input.IndexOf('*', i + 1);
                        if (nextStar != -1) endIndex = nextStar;
                    }
                    else
                    {
                        char opener = c;
                        char closer = (c == '(') ? ')' : (c == '（') ? '）' : '】';
                        int depth = 1;

                        for (int j = i + 1; j < input.Length; j++)
                        {
                            if (input[j] == opener)
                            {
                                depth++;
                            }
                            else if (input[j] == closer)
                            {
                                depth--;
                                if (depth == 0)
                                {
                                    endIndex = j;
                                    break;
                                }
                            }
                        }
                    }

                    if (endIndex != -1)
                    {
                        string fullMatch = input.Substring(i, endIndex - i + 1);
                        bool isKaomoji = IsLikelyKaomoji(fullMatch);

                        if (isKaomoji)
                        {
                            if (settings.enableKaomojiColoring)
                            {
                                Color finalColor = settings.kaomojiColor;
                                string hex = ColorUtility.ToHtmlStringRGBA(finalColor);
                                string styledPart = $"<color=#{hex}>{fullMatch}</color>";
                                if (settings.kaomojiBold) styledPart = $"<b>{styledPart}</b>";
                                sb.Append(styledPart);
                            }
                            else
                            {
                                sb.Append(fullMatch);
                            }
                        }
                        else
                        {
                            Color finalColor = settings.selfTalkColor;
                            string hex = ColorUtility.ToHtmlStringRGBA(finalColor);
                            string styledPart = $"<color=#{hex}>{fullMatch}</color>";
                            if (settings.selfTalkItalic) styledPart = $"<i>{styledPart}</i>";
                            if (settings.selfTalkBold) styledPart = $"<b>{styledPart}</b>";
                            sb.Append(styledPart);
                        }

                        i = endIndex + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsLikelyKaomoji(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length < 2) return false;
            string content = input.Substring(1, input.Length - 2);

            if (Regex.IsMatch(content, @"[\u4e00-\u9fa5]"))
            {
                string fakeKanji = "口皿益罒一二三川丁人入八乂";
                bool hasRealMeaning = false;
                foreach (char c in content)
                {
                    if (c >= 0x4e00 && c <= 0x9fa5 && !fakeKanji.Contains(c))
                    {
                        hasRealMeaning = true; break;
                    }
                }
                if (hasRealMeaning) return false;
            }

            char[] unambiguousSymbols = new char[] {
                '_', '^', '=', '-', '°', 'º', '•', '●', '⊙', '✪', '○', '⊕', '◎', '˙', '¨',
                '˘', '´', '`', '¯', '￣', '‾', '＿', '—', '–', '~', '～', 'ˇ', 'ˆ',
                '/', '\\', 'ノ', 'ﾉ', 'ヽ', '╰', '╯', '╭', '╮', '┐', '┌', '└', '┘',
                ';', '；', 'ㆀ', '#', '╬', 'ﾒ', '凸',
                '♥', '♡', '❤', '❥', '★', '☆', '✧', '✦', '⋆', '♪', '♩', '♫', '♬',
                '⁺', '⁻', 'ˊ', 'ˋ', 'ᐟ', 'ᐠ', 'ᑊ',
                '□', '▽', '△', '▲', '▼', '◄', '►', '︿', '﹀', '︹', '︺', '◇', '◆',
                '╥', '∏', 'π', '┬', '┴', '┰', '┯', '┻', '━', '┳', '┷', '┸',
                '|', '¦', '┆', '┊', '│', '┃',
                '←', '↑', '→', '↓', '↔', '↕',
                '░', '▒', '▓', '█', '▌', '▐', '▀', '▄',
                '◞', '◟', '◜', '◝', '‸', '◠', '◡', '⊂', '⊃', '∩', '∪',
                'ω', '∀', 'Д', 'д', 'ε', 'з', 'Ψ', 'Φ', 'φ', 'ι', 'Θ', 'θ', 'Ω', 'ʊ',
                'ー', 'ヮ', 'ワ', 'ロ', 'ミ', 'シ', 'ツ',
                'ಠ', 'ರ', 'ೃ', 'ಥ', 'ఠ', 'ల', 'ஂ', 'ൠ', 'ළ', 'ஞ',
                '๑', 'و', '٩', '۶', '໒', '꒡', 'ꆜ', '꒝', '꒢',
                '༶', '༚', '෴', 'ᶘ', 'ᶅ', 'ᴥ', 'ʋ', 'ʕ', 'ʔ',
                'Ꙩ', 'ꙩ', 'Ꙫ', 'ꙫ', 'ʘ',
                '©', '®', '†', '‡', '§', '¶', '∞', '', '✕', '✖', '✚', '✛',
                '✿', '❀', '❁', '✾', '❃', '❋', '⚘', '☁', '☀', '☂', '☃', '☄'
            };
            if (content.IndexOfAny(unambiguousSymbols) >= 0) return true;

            string[] riskyKaomojiParts = new string[] {
                "QAQ", "TAT", "QWQ", "XD", "ORZ", "OTL", "XDD", "T_T", "P_P", "O_O",
                "UWU", "OWO", "TVT", "O.O", "Q.Q"
            };
            string upper = content.ToUpper();
            foreach (var tk in riskyKaomojiParts) if (upper.Contains(tk)) return true;

            if (!Regex.IsMatch(content, @"[a-zA-Z]")) return true;
            return false;
        }

        private static string GenerateRainbowText(string input)
        {
            float currentTime = Time.realtimeSinceStartup;
            string cacheKey = input;
            if (_rainbowCache.TryGetValue(cacheKey, out RainbowCacheEntry cached))
            {
                if (currentTime - cached.lastUpdateTime < RainbowCacheUpdateInterval)
                {
                    return cached.cachedText;
                }
            }
            StringBuilder sb = new StringBuilder();
            float timeOffset = currentTime * 2f;
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
            string result = sb.ToString();
            _rainbowCache[cacheKey] = new RainbowCacheEntry { cachedText = result, lastUpdateTime = currentTime };
            if (_rainbowCache.Count > 50)
            {
                var keysToRemove = _rainbowCache.OrderBy(x => x.Value.lastUpdateTime).Take(20).Select(x => x.Key).ToList();
                foreach (var key in keysToRemove) _rainbowCache.Remove(key);
            }
            return result;
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

        private static string GetArchiveName()
        {
            if (Find.FactionManager != null && Faction.OfPlayer != null)
                return Faction.OfPlayer.Name;
            return "UnknownSave";
        }

        private static bool isChineseLanguage()
        {
            if (LanguageDatabase.activeLanguage == null) return false;
            string folder = LanguageDatabase.activeLanguage.folderName ?? "";
            string friendly = LanguageDatabase.activeLanguage.FriendlyNameEnglish ?? "";
            return folder.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   friendly.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void GetExportTitles(out string tabTitle, out string pageHeader, out string subHeader)
        {
            string dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string saveName = GetArchiveName();
            if (isChineseLanguage()) { tabTitle = $"言出多彩记录-{dateStr} by Maiya0126"; pageHeader = "由边缘世谭-言出多彩导出 by Maiya0126"; subHeader = $"{saveName}-{dateStr}"; }
            else { tabTitle = $"RimTalk DynamicColors Log-{dateStr} by Maiya0126"; pageHeader = "Exported by RimTalk DynamicColors by Maiya0126"; subHeader = $"{saveName}-{dateStr}"; }
        }

        public static void ApplyTabVisibility()
        {
            MainButtonDef def = DefDatabase<MainButtonDef>.GetNamed("RTDC_HistoryTab", false);
            if (def != null) { def.buttonVisible = settings.showHistoryTab; }
        }

        // ==============================================================
        // [修改核心] 历史记录绘制 (加入了头像渲染支持)
        // ==============================================================
        public static void DrawHistoryArea(Listing_Standard listing, float width, float viewHeightParam = 300f)
        {
            listing.Label("<b>" + "RTDC_ChatHistoryViewer".Translate() + "</b>");

            string bufferCount = "RTDC_BufferStatus".Translate(SessionHistory.Count, 200);
            Widgets.Label(listing.GetRect(20f), bufferCount);

            Rect filterRow = listing.GetRect(30f);
            float filterW = (width - 20f) / 3f;

            HashSet<string> speakers = new HashSet<string>();
            foreach (var item in SessionHistory) if (!string.IsNullOrEmpty(item.PawnName)) speakers.Add(item.PawnName);
            List<string> speakerList = speakers.OrderBy(x => x).ToList();
            speakerList.Insert(0, "All");

            if (Widgets.ButtonText(new Rect(filterRow.x, filterRow.y, filterW - 5f, 24f), "Filter: " + filterPawnName))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (var name in speakerList) options.Add(new FloatMenuOption(name, () => filterPawnName = name));
                Find.WindowStack.Add(new FloatMenu(options));
            }

            if (filterPawnName != "All")
            {
                string modeLabel = filterDimMode ? "Mode: Dim Others" : "Mode: Hide Others";
                if (isChineseLanguage()) modeLabel = filterDimMode ? "模式: 淡化他人" : "模式: 隐藏他人";
                if (Widgets.ButtonText(new Rect(filterRow.x + filterW, filterRow.y, filterW - 5f, 24f), modeLabel)) filterDimMode = !filterDimMode;
            }

            Rect autoExportRect = new Rect(filterRow.x + filterW * 2, filterRow.y, filterW, 24f);
            Widgets.CheckboxLabeled(autoExportRect, "RTDC_QuickAutoExport".Translate(), ref settings.autoExportHistory);
            TooltipHandler.TipRegion(autoExportRect, "RTDC_QuickAutoExportTip".Translate());

            listing.Gap(5f);

            listing.Label("RTDC_HistoryInfo".Translate(), -1f, null);

            Rect viewerRect = listing.GetRect(viewHeightParam);
            Widgets.DrawBoxSolid(viewerRect, new Color(0.1f, 0.1f, 0.1f, 0.5f));
            Widgets.DrawBox(viewerRect);

            List<LogItem> displayList = new List<LogItem>();
            if (filterPawnName == "All") displayList = SessionHistory;
            else
            {
                if (filterDimMode) displayList = SessionHistory;
                else displayList = SessionHistory.Where(x => x.PawnName == filterPawnName).ToList();
            }

            float avatarSpace = settings.showAvatarInHistory ? 28f : 0f;
            float viewWidth = viewerRect.width - 20f;

            bool needRecalculate = _lastHistoryCount != displayList.Count || _lastFilterName != filterPawnName || Math.Abs(_lastViewWidth - viewWidth) > 1f;
            if (needRecalculate)
            {
                _cachedRowHeights.Clear();
                _cachedRowYPositions.Clear();
                _cachedTotalHeight = 0f;

                for (int i = 0; i < displayList.Count; i++)
                {
                    var item = displayList[i];
                    Vector2 nameSize = Text.CalcSize(item.PawnName);
                    float textWidth = viewWidth - (nameSize.x + 15f) - avatarSpace;
                    float textHeight = Text.CalcHeight(item.CleanContent, textWidth);
                    float rowHeight = Mathf.Max(24f, textHeight);

                    _cachedRowYPositions.Add(_cachedTotalHeight);
                    _cachedRowHeights.Add(rowHeight);
                    _cachedTotalHeight += rowHeight;
                }

                _lastHistoryCount = displayList.Count;
                _lastFilterName = filterPawnName;
                _lastViewWidth = viewWidth;
            }

            Rect contentRect = new Rect(0, 0, viewWidth, _cachedTotalHeight);
            Widgets.BeginScrollView(viewerRect, ref logViewerScrollPos, contentRect);

            float visibleTop = logViewerScrollPos.y;
            float visibleBottom = logViewerScrollPos.y + viewerRect.height;

            int firstVisibleIndex = -1;
            int lastVisibleIndex = -1;
            for (int i = 0; i < _cachedRowYPositions.Count; i++)
            {
                float rowTop = _cachedRowYPositions[i];
                float rowBottom = rowTop + _cachedRowHeights[i];
                if (rowBottom >= visibleTop && rowTop <= visibleBottom)
                {
                    if (firstVisibleIndex < 0) firstVisibleIndex = i;
                    lastVisibleIndex = i;
                }
            }

            if (firstVisibleIndex < 0) firstVisibleIndex = 0;
            if (lastVisibleIndex < 0) lastVisibleIndex = displayList.Count - 1;
            lastVisibleIndex = Mathf.Min(lastVisibleIndex, displayList.Count - 1);

            for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
            {
                var item = displayList[i];
                float curY = _cachedRowYPositions[i];
                float rowHeight = _cachedRowHeights[i];
                bool isDimmed = (filterPawnName != "All" && filterDimMode && item.PawnName != filterPawnName);

                Color originalGuiColor = GUI.color;
                if (isDimmed) GUI.color = new Color(1f, 1f, 1f, 0.3f);

                string contentToShow = isDimmed ? item.CleanContent : item.Content;

                Vector2 nameSize = Text.CalcSize(item.PawnName);
                float textWidth = viewWidth - (nameSize.x + 15f) - avatarSpace;

                Rect lineRect = new Rect(5f, curY, viewWidth, rowHeight);

                if (settings.showAvatarInHistory && item.SpeakerPawn != null && item.SpeakerPawn.SpawnedOrAnyParentSpawned)
                {
                    Rect avatarRect = new Rect(lineRect.x, lineRect.y, 24f, 24f);
                    try
                    {
                        RenderTexture portrait = PortraitsCache.Get(item.SpeakerPawn, new Vector2(24f, 24f), Rot4.South);
                        if (portrait != null)
                        {
                            GUI.DrawTexture(avatarRect, portrait);
                        }
                    }
                    catch
                    {
                        Widgets.ThingIcon(avatarRect, item.SpeakerPawn);
                    }
                }

                Rect nameRect = new Rect(lineRect.x + avatarSpace, lineRect.y, nameSize.x + 10f, rowHeight);
                Color nameColorToDraw = isDimmed ? item.NameColor * 0.6f : item.NameColor;
                nameColorToDraw.a = isDimmed ? 0.3f : 1f;
                Color vanillaColor = Color.white;
                if (!isDimmed && item.SpeakerPawn != null) vanillaColor = PawnNameColorUtility.PawnNameColorOf(item.SpeakerPawn);
                string nameHex = ColorUtility.ToHtmlStringRGB(nameColorToDraw);
                string bracketHex = ColorUtility.ToHtmlStringRGB(vanillaColor);
                GUI.color = Color.white;
                Widgets.Label(nameRect, $"<color=#{bracketHex}>[</color><color=#{nameHex}>{item.PawnName}</color><color=#{bracketHex}>]</color>");

                // 内容文本框
                GUI.color = isDimmed ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
                Rect diagRect = new Rect(nameRect.xMax, lineRect.y, textWidth, rowHeight);
                Widgets.Label(diagRect, contentToShow);

                GUI.color = originalGuiColor;
            }
            Widgets.EndScrollView();

            listing.Gap(10f);
            listing.Label("<b>" + "RTDC_ExportPathSetting".Translate() + "</b>");
            string currentPath = string.IsNullOrEmpty(settings.customExportPath) ? GenFilePaths.SaveDataFolderPath : settings.customExportPath;
            Rect btnRowRect = listing.GetRect(30f);
            float btnWidth = (btnRowRect.width - 10f) / 2f;

            if (Widgets.ButtonText(new Rect(btnRowRect.x, btnRowRect.y, btnWidth, 30f), "RTDC_SetPath".Translate()))
            {
                Find.WindowStack.Add(new Dialog_Input("RTDC_SetPath".Translate(), "Path", currentPath, (val) => {
                    if (Directory.Exists(val)) settings.customExportPath = val; else Messages.Message("RTDC_PathInvalid".Translate(), MessageTypeDefOf.RejectInput, false);
                }));
            }
            if (Widgets.ButtonText(new Rect(btnRowRect.x + btnWidth + 10f, btnRowRect.y, btnWidth, 30f), "RTDC_OpenFolder".Translate()))
            {
                if (Directory.Exists(currentPath)) Application.OpenURL(currentPath); else Messages.Message("RTDC_PathInvalid".Translate(), MessageTypeDefOf.RejectInput, false);
            }

            listing.Gap(5f);
            Rect textRect = listing.GetRect(24f);
            GUI.color = Color.gray; Widgets.Label(textRect, "RTDC_CurrentPathLabel".Translate() + " " + currentPath); GUI.color = Color.white;
            if (listing.ButtonText("RTDC_ResetPath".Translate())) settings.customExportPath = "";

            listing.Gap(10f);
            listing.Label("<b>" + "RTDC_LogTools".Translate() + "</b>");
            Rect copyBtnRect2 = listing.GetRect(30f);
            float quarterWidth = width / 4f - 5f;
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x, copyBtnRect2.y, quarterWidth, 30f), "RTDC_CopyPlain".Translate())) CopyChatLogToClipboard(false);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + quarterWidth + 5f, copyBtnRect2.y, quarterWidth, 30f), "RTDC_CopyRich".Translate())) CopyChatLogToClipboard(true);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + (quarterWidth + 5f) * 2, copyBtnRect2.y, quarterWidth, 30f), "RTDC_ExportTxt".Translate())) ExportChatLog(false);
            if (Widgets.ButtonText(new Rect(copyBtnRect2.x + (quarterWidth + 5f) * 3, copyBtnRect2.y, quarterWidth, 30f), "RTDC_ExportHtml".Translate())) ExportChatLog(true);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (settings == null) settings = GetSettings<DynamicColorSettings>();

            Rect viewRect = new Rect(0, 0, inRect.width - 16f, 1350f);
            Widgets.BeginScrollView(inRect, ref mainScrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            Text.Font = GameFont.Medium;
            listing.Label("RTDC_SettingsTitle".Translate());
            Text.Font = GameFont.Small;
            listing.GapLine(6f);

            listing.Label("<b>" + "RTDC_BasicSettings".Translate() + " / " + "RTDC_HistorySettings".Translate() + "</b>");

            // 行1
            DrawTwoColumnCheckbox(listing,
                "RTDC_EnableMod".Translate(), ref settings.isGlobalEnabled, "RTDC_EnableModDesc".Translate(),
                "RTDC_ShowHistoryTab".Translate(), ref settings.showHistoryTab, "RTDC_ShowHistoryTabDesc".Translate());
            if (settings.showHistoryTab != DefDatabase<MainButtonDef>.GetNamed("RTDC_HistoryTab").buttonVisible) ApplyTabVisibility();

            // 行2：加入显示头像开关
            Rect row2 = listing.GetRect(24f);
            float halfW = row2.width / 2f;
            Widgets.CheckboxLabeled(new Rect(row2.x, row2.y, halfW - 5f, 24f), "RTDC_AutoExportHistory".Translate(), ref settings.autoExportHistory);
            TooltipHandler.TipRegion(new Rect(row2.x, row2.y, halfW - 5f, 24f), "RTDC_AutoExportHistoryDesc".Translate());

            Widgets.CheckboxLabeled(new Rect(row2.x + halfW, row2.y, halfW - 5f, 24f), "RTDC_ShowAvatarInHistory".Translate(), ref settings.showAvatarInHistory);

            // 行3：Bubbles 检测
            bool hasBubbles = hasBubblesMod;
            string bubbleLabel = hasBubbles ? "RTDC_EnableBubblesSync".Translate() : ("RTDC_BubblesNotDetected".Translate() + " (Click Retry)");
            Rect row3 = listing.GetRect(24f);

            if (hasBubbles)
            {
                Widgets.CheckboxLabeled(new Rect(row3.x, row3.y, halfW - 5f, 24f), bubbleLabel, ref settings.enableBubblesSync);
                TooltipHandler.TipRegion(new Rect(row3.x, row3.y, halfW - 5f, 24f), "RTDC_EnableBubblesSyncDesc".Translate());
            }
            else
            {
                if (Widgets.ButtonText(new Rect(row3.x, row3.y, halfW - 5f, 24f), bubbleLabel)) TryPatchInteractionBubbles();
            }

            DrawTwoColumnCheckbox(listing,
                "RTDC_EnableRimTalkNameColoring".Translate(), ref settings.enableRimTalkNameColoring, "RTDC_EnableRimTalkNameColoringDesc".Translate(),
                "RTDC_EnableChatColoring".Translate(), ref settings.enableChatColoring, "RTDC_EnableChatColoringDesc".Translate());

            listing.Gap(4f);
            if (listing.ButtonText("RTDC_OpenHistoryWindow".Translate(), "RTDC_OpenHistoryWindowDesc".Translate()))
            {
                Window existing = Find.WindowStack.WindowOfType<RimTalkHistoryWindow>();
                if (existing != null) existing.Close(); else Find.WindowStack.Add(new RimTalkHistoryWindow());
            }
            listing.GapLine(6f);

            listing.Label("<b>" + "RTDC_ColorRules".Translate() + "</b>");
            DrawTwoColumnCheckbox(listing,
                "RTDC_EnableForPrisoners".Translate(), ref settings.enableForPrisoners, null,
                "RTDC_EnableForSlaves".Translate(), ref settings.enableForSlaves, null);
            DrawTwoColumnCheckbox(listing,
                "RTDC_EnableForGuests".Translate(), ref settings.enableForGuests, null,
                "RTDC_EnableForFriendlies".Translate(), ref settings.enableForFriendlies, null);

            Rect rowColor = listing.GetRect(24f);
            Widgets.CheckboxLabeled(new Rect(rowColor.x, rowColor.y, halfW - 5f, 24f), "RTDC_EnableForEnemies".Translate(), ref settings.enableForEnemies);
            Widgets.CheckboxLabeled(new Rect(rowColor.x + halfW, rowColor.y, halfW - 5f, 24f), "RTDC_EnableForNonHumans".Translate(), ref settings.enableForNonHumans);

            if (settings.enableForNonHumans)
            {
                listing.Gap(2f);
                Rect nhRow = listing.GetRect(24f);
                Rect colorBtnRect = new Rect(nhRow.x + halfW, nhRow.y, halfW - 35f, 24f);
                if (Widgets.ButtonText(colorBtnRect, "RTDC_SelectNonHumanColor".Translate()))
                    Find.WindowStack.Add(new ColorPickerWindow(settings.nonHumanDefaultColor, DynamicColorSettings.DefaultNonHumanColor, "Non-Human", (c) => settings.nonHumanDefaultColor = c));
                Widgets.DrawBoxSolid(new Rect(nhRow.x + halfW + halfW - 30f, nhRow.y, 24f, 24f), settings.nonHumanDefaultColor);
            }

            listing.Gap(4f);
            DrawTwoColumnCheckbox(listing,
                "RTDC_UseFactionColor".Translate(), ref settings.useFactionColor, "RTDC_UseFactionColorDesc".Translate(),
                "RTDC_AutoFavColor".Translate(), ref settings.autoApplyFavColor, "RTDC_AutoFavColorDesc".Translate());

            if (settings.autoApplyFavColor)
                listing.CheckboxLabeled("  ↳ " + "RTDC_AutoApplyBold".Translate(), ref settings.autoApplyBold);

            if (settings.autoApplyFavColor != (bool)settings.autoApplyFavColor) settings.RebuildCache();

            listing.GapLine(6f);

            listing.Label("<b>" + "RTDC_StyleSettings".Translate() + "</b>");

            Rect stRow = listing.GetRect(24f);
            Widgets.CheckboxLabeled(new Rect(stRow.x, stRow.y, halfW - 5f, 24f), "RTDC_EnableSelfTalkColor".Translate(), ref settings.enableSelfTalkColor);

            if (settings.enableSelfTalkColor)
            {
                if (Widgets.ButtonText(new Rect(stRow.x + halfW, stRow.y, halfW - 35f, 24f), "RTDC_SelectSelfTalkColor".Translate()))
                    Find.WindowStack.Add(new ColorPickerWindow(settings.selfTalkColor, DynamicColorSettings.DefaultSelfTalkColor, "(* Action *)", (c) => settings.selfTalkColor = c));
                Widgets.DrawBoxSolid(new Rect(stRow.x + halfW + halfW - 30f, stRow.y, 24f, 24f), settings.selfTalkColor);
                listing.Gap(2f);
                DrawTwoColumnCheckbox(listing,
                    "RTDC_Bold".Translate(), ref settings.selfTalkBold, null,
                    "RTDC_SelfTalkItalic".Translate(), ref settings.selfTalkItalic, null);
            }

            listing.Gap(6f);

            Rect kmRow = listing.GetRect(24f);
            Widgets.CheckboxLabeled(new Rect(kmRow.x, kmRow.y, halfW - 5f, 24f), "RTDC_EnableKaomojiColoring".Translate(), ref settings.enableKaomojiColoring);

            if (settings.enableKaomojiColoring)
            {
                if (Widgets.ButtonText(new Rect(kmRow.x + halfW, kmRow.y, halfW - 35f, 24f), "RTDC_SelectKaomojiColor".Translate()))
                    Find.WindowStack.Add(new ColorPickerWindow(settings.kaomojiColor, DynamicColorSettings.DefaultKaomojiColor, "(>_<)", (c) => settings.kaomojiColor = c));
                Widgets.DrawBoxSolid(new Rect(kmRow.x + halfW + halfW - 30f, kmRow.y, 24f, 24f), settings.kaomojiColor);

                listing.Gap(2f);
                bool fakeVal = false;
                DrawTwoColumnCheckbox(listing,
                    "RTDC_Bold".Translate(), ref settings.kaomojiBold, null,
                    null, ref fakeVal, null);
            }

            listing.Gap(6f);
            DrawTwoColumnCheckbox(listing,
                "RTDC_EnableRainbow".Translate(), ref settings.enableRainbowEffect, "RTDC_EnableRainbowDesc".Translate(),
                "RTDC_EnableBW".Translate(), ref settings.enableBlackWhiteEffect, "RTDC_EnableBWDesc".Translate());

            listing.GapLine(6f);

            listing.Label("<b>" + "RTDC_BracketStyleSettings".Translate() + "</b>");
            listing.Label("RTDC_BracketStyleDesc".Translate());
            listing.Gap(4f);

            Rect bsInputRow = listing.GetRect(26f);
            float bsFieldW = (bsInputRow.width - 90f) / 2f;
            inputOpenSymbolBuffer = Widgets.TextField(new Rect(bsInputRow.x, bsInputRow.y, bsFieldW, 26f), inputOpenSymbolBuffer ?? "");
            Widgets.Label(new Rect(bsInputRow.x + bsFieldW + 2f, bsInputRow.y, 16f, 26f), "~");
            inputCloseSymbolBuffer = Widgets.TextField(new Rect(bsInputRow.x + bsFieldW + 20f, bsInputRow.y, bsFieldW, 26f), inputCloseSymbolBuffer ?? "");
            if (Widgets.ButtonText(new Rect(bsInputRow.xMax - 60f, bsInputRow.y, 60f, 26f), "RTDC_Add".Translate()))
            {
                if (!string.IsNullOrEmpty(inputOpenSymbolBuffer) && !string.IsNullOrEmpty(inputCloseSymbolBuffer))
                {
                    settings.bracketStyleEntries.Add(new BracketStyleEntry(inputOpenSymbolBuffer, inputCloseSymbolBuffer, new Color(0.6f, 0.6f, 0.6f, 1f)));
                    inputOpenSymbolBuffer = "";
                    inputCloseSymbolBuffer = "";
                }
            }

            if (settings.bracketStyleEntries.Count > 0)
            {
                float bsListHeight = settings.bracketStyleEntries.Count * 28f;
                Rect bsListRect = listing.GetRect(Mathf.Min(bsListHeight, 200f));
                Widgets.DrawBoxSolid(bsListRect, new Color(0.1f, 0.1f, 0.1f, 0.2f));
                Widgets.DrawBox(bsListRect);

                Rect bsViewRect = new Rect(0, 0, bsListRect.width - 20f, bsListHeight);
                Widgets.BeginScrollView(bsListRect, ref bracketListScrollPos, bsViewRect);
                DrawBracketStyleList(settings.bracketStyleEntries, bsViewRect.width);
                Widgets.EndScrollView();
            }

            if (listing.ButtonText("RTDC_ClearBracketStyles".Translate()))
            {
                settings.bracketStyleEntries.Clear();
            }

            listing.GapLine(6f);

            listing.Label("<b>" + "RTDC_DataManage".Translate() + "</b>");
            listing.Gap(4f);

            Rect dataSectionRect = listing.GetRect(420f);

            float gap = 10f;
            float colWidth = (dataSectionRect.width - gap) / 2f;

            Rect leftCol = new Rect(dataSectionRect.x, dataSectionRect.y, colWidth, dataSectionRect.height);
            Rect rightCol = new Rect(dataSectionRect.x + colWidth + gap, dataSectionRect.y, colWidth, dataSectionRect.height);

            DoNameManagementColumn(leftCol);
            DoKeywordManagementColumn(rightCol);

            if (listing.ButtonText("RTDC_ClearAll".Translate()))
            {
                settings.nameEntries.Clear(); settings.keywordEntries.Clear();
                settings.RebuildCache();
                Messages.Message("RTDC_Cleared".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }

            listing.End();
            Widgets.EndScrollView();
        }

        private void DoNameManagementColumn(Rect rect)
        {
            float curY = rect.y;
            Widgets.Label(new Rect(rect.x, curY, rect.width, 24f), "RTDC_ManageNames".Translate());
            curY += 28f;

            float btnW = 60f;
            float inputW = rect.width - btnW - 5f;
            inputNameBuffer = Widgets.TextField(new Rect(rect.x, curY, inputW, 26f), inputNameBuffer ?? "");
            if (Widgets.ButtonText(new Rect(rect.x + inputW + 5f, curY, btnW, 26f), "RTDC_Add".Translate()))
            { AddNewEntry(settings.nameEntries, inputNameBuffer); inputNameBuffer = ""; }
            curY += 30f;

            float actionBtnW = (rect.width - 5f) / 2f;
            if (Widgets.ButtonText(new Rect(rect.x, curY, actionBtnW, 26f), "RTDC_ImportColonists".Translate())) ImportAllPawns();
            if (Widgets.ButtonText(new Rect(rect.x + actionBtnW + 5f, curY, actionBtnW, 26f), "RTDC_ClearNames".Translate()))
            { settings.nameEntries.Clear(); settings.RebuildCache(); }
            curY += 30f;

            float listHeight = rect.height - (curY - rect.y);
            Rect outRect = new Rect(rect.x, curY, rect.width, listHeight);

            Widgets.DrawBoxSolid(outRect, new Color(0.1f, 0.1f, 0.1f, 0.2f));
            Widgets.DrawBox(outRect);

            float contentHeight = settings.nameEntries.Count * 28f;
            Rect viewRect = new Rect(0, 0, rect.width - 20f, contentHeight);

            Widgets.BeginScrollView(outRect, ref nameListScrollPos, viewRect);
            DrawCustomListContent(settings.nameEntries, viewRect.width, nameListScrollPos, outRect.height);
            Widgets.EndScrollView();
        }

        private void DoKeywordManagementColumn(Rect rect)
        {
            float curY = rect.y;
            Widgets.Label(new Rect(rect.x, curY, rect.width, 24f), "RTDC_ManageKeywords".Translate());
            curY += 28f;

            float btnW = 60f;
            float inputW = rect.width - btnW - 5f;
            inputKeywordBuffer = Widgets.TextField(new Rect(rect.x, curY, inputW, 26f), inputKeywordBuffer ?? "");
            if (Widgets.ButtonText(new Rect(rect.x + inputW + 5f, curY, btnW, 26f), "RTDC_Add".Translate()))
            { AddNewEntry(settings.keywordEntries, inputKeywordBuffer); inputKeywordBuffer = ""; }
            curY += 30f;

            curY += 30f;

            float listHeight = rect.height - (curY - rect.y);
            Rect outRect = new Rect(rect.x, curY, rect.width, listHeight);

            Widgets.DrawBoxSolid(outRect, new Color(0.1f, 0.1f, 0.1f, 0.2f));
            Widgets.DrawBox(outRect);

            float contentHeight = settings.keywordEntries.Count * 28f;
            Rect viewRect = new Rect(0, 0, rect.width - 20f, contentHeight);

            Widgets.BeginScrollView(outRect, ref keywordListScrollPos, viewRect);
            DrawCustomListContent(settings.keywordEntries, viewRect.width, keywordListScrollPos, outRect.height);
            Widgets.EndScrollView();
        }

        private void DrawBracketStyleList(List<BracketStyleEntry> entries, float width)
        {
            if (entries == null) return;
            List<BracketStyleEntry> toRemove = new List<BracketStyleEntry>();
            float curY = 0f;
            float rowHeight = 28f;
            float delW = 40f;
            float colorBoxW = 24f;
            float colorBtnW = 50f;
            float boldW = 24f;
            float italicW = 24f;
            float arrowW = 16f;
            float symbolW = (width - delW - colorBoxW - colorBtnW - boldW - italicW - arrowW - 25f) / 2f;

            for (int i = 0; i < entries.Count; i++)
            {
                if (curY + rowHeight < bracketListScrollPos.y || curY > bracketListScrollPos.y + 200f)
                {
                    curY += rowHeight;
                    continue;
                }

                BracketStyleEntry entry = entries[i];
                if (entry == null) continue;

                float curX = 0f;

                entry.openSymbol = Widgets.TextField(new Rect(curX, curY, symbolW, 24f), entry.openSymbol ?? "");
                curX += symbolW + 2f;

                Widgets.Label(new Rect(curX, curY, arrowW, 24f), "~");
                curX += arrowW + 2f;

                entry.closeSymbol = Widgets.TextField(new Rect(curX, curY, symbolW, 24f), entry.closeSymbol ?? "");
                curX += symbolW + 5f;

                if (Widgets.ButtonText(new Rect(curX, curY, colorBtnW, 24f), "Color"))
                {
                    Find.WindowStack.Add(new ColorPickerWindow(entry.color, Color.gray, $"{entry.openSymbol}..{entry.closeSymbol}", (c) => entry.color = c));
                }
                curX += colorBtnW + 2f;

                Widgets.DrawBoxSolid(new Rect(curX, curY, colorBoxW, 24f), entry.color);
                curX += colorBoxW + 5f;

                Widgets.Checkbox(new Vector2(curX, curY), ref entry.isBold);
                TooltipHandler.TipRegion(new Rect(curX, curY, boldW, 24f), "RTDC_Bold".Translate());
                curX += boldW + 3f;

                Widgets.Checkbox(new Vector2(curX, curY), ref entry.isItalic);
                TooltipHandler.TipRegion(new Rect(curX, curY, italicW, 24f), "RTDC_Italic".Translate());
                curX += italicW + 3f;

                if (Widgets.ButtonText(new Rect(curX, curY, delW, 24f), "Del"))
                    toRemove.Add(entry);

                curY += rowHeight;
            }

            foreach (var item in toRemove) entries.Remove(item);
        }

        private void DrawTwoColumnCheckbox(Listing_Standard listing, string label1, ref bool check1, string tip1, string label2, ref bool check2, string tip2)
        {
            Rect rect = listing.GetRect(24f);
            float colWidth = rect.width / 2f;
            if (label1 != null)
            {
                Rect left = new Rect(rect.x, rect.y, colWidth - 5f, 24f);
                Widgets.CheckboxLabeled(left, label1, ref check1);
                if (!string.IsNullOrEmpty(tip1)) TooltipHandler.TipRegion(left, tip1);
            }
            if (label2 != null)
            {
                Rect right = new Rect(rect.x + colWidth, rect.y, colWidth - 5f, 24f);
                Widgets.CheckboxLabeled(right, label2, ref check2);
                if (!string.IsNullOrEmpty(tip2)) TooltipHandler.TipRegion(right, tip2);
            }
        }

        private static void DrawCustomListContent(List<ColorEntry> entries, float width, Vector2 scrollPos, float viewHeight)
        {
            if (entries == null) return;
            List<ColorEntry> toRemove = new List<ColorEntry>();
            bool changed = false;
            float curY = 0f;
            float rowHeight = 28f;

            float delW = 40f;
            float boldW = 24f;
            float colorBoxW = 24f;
            float colorBtnW = 50f;
            float textW = width - delW - boldW - colorBoxW - colorBtnW - 15f;

            for (int i = 0; i < entries.Count; i++)
            {
                if (curY + rowHeight < scrollPos.y || curY > scrollPos.y + viewHeight)
                {
                    curY += rowHeight;
                    continue;
                }

                ColorEntry entry = entries[i];
                if (entry == null) continue;

                float curX = 0f;
                string newText = Widgets.TextField(new Rect(curX, curY, textW, 24f), entry.text);
                if (newText != entry.text) { entry.text = newText; changed = true; }
                curX += textW + 3f;

                if (Widgets.ButtonText(new Rect(curX, curY, colorBtnW, 24f), "Color"))
                {
                    Find.WindowStack.Add(new ColorPickerWindow(entry.color, Color.white, entry.text, (newColor) => {
                        entry.color = newColor;
                        settings.RebuildCache();
                    }));
                }
                curX += colorBtnW + 2f;
                Widgets.DrawBoxSolid(new Rect(curX, curY, colorBoxW, 24f), entry.color);
                curX += colorBoxW + 5f;

                Widgets.Checkbox(new Vector2(curX, curY), ref entry.isBold);
                TooltipHandler.TipRegion(new Rect(curX, curY, boldW, 24f), "RTDC_Bold".Translate());
                curX += boldW + 3f;

                if (Widgets.ButtonText(new Rect(curX, curY, delW, 24f), "Del"))
                { toRemove.Add(entry); changed = true; }

                curY += rowHeight;
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
            GetExportTitles(out string _, out string pageHeader, out string subHeader);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(pageHeader);
            sb.AppendLine(subHeader);
            sb.AppendLine(new string('-', 30));

            List<LogItem> listToCopy = SessionHistory;
            if (filterPawnName != "All" && !filterDimMode) listToCopy = SessionHistory.Where(x => x.PawnName == filterPawnName).ToList();

            foreach (var msg in listToCopy)
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
                if (!silent) Find.TickManager.Pause();
                GetExportTitles(out string tabTitle, out string pageHeader, out string subHeader);
                string timestamp = DateTime.Now.ToString("MM-dd_HH-mm-ss");
                string ext = toHtml ? "html" : "txt";
                string filename = $"RTDC Auto-{timestamp}.{ext}";
                string basePath = string.IsNullOrEmpty(settings.customExportPath) ? GenFilePaths.SaveDataFolderPath : settings.customExportPath;
                if (!Directory.Exists(basePath)) Directory.CreateDirectory(basePath);
                string path = Path.Combine(basePath, filename);
                StringBuilder sb = new StringBuilder();

                if (toHtml)
                {
                    sb.AppendLine("<!DOCTYPE html>");
                    sb.AppendLine("<html><head><meta charset='utf-8'>");
                    sb.AppendLine($"<title>{tabTitle}</title>");
                    sb.AppendLine("<style>");
                    sb.AppendLine("body { background-color: #222; color: #eee; font-family: sans-serif; padding: 20px; }");
                    sb.AppendLine(".msg { margin-bottom: 5px; transition: opacity 0.3s; }");
                    sb.AppendLine(".name { font-weight: bold; margin-right: 5px; }");
                    sb.AppendLine("h1, h2 { color: #fff; margin: 5px 0; }");
                    sb.AppendLine("h1 { font-size: 24px; }");
                    sb.AppendLine("h2 { font-size: 16px; color: #aaa; margin-bottom: 20px; border-bottom: 1px solid #444; padding-bottom: 10px; }");
                    sb.AppendLine(".toolbar { background: #333; padding: 10px; margin-bottom: 20px; border-radius: 5px; position: sticky; top: 0; z-index: 100; border-bottom: 2px solid #555; }");
                    sb.AppendLine("select, button { padding: 5px 10px; font-size: 14px; margin-right: 10px; background: #444; color: white; border: 1px solid #666; cursor: pointer; }");
                    sb.AppendLine(".dimmed { opacity: 0.2; }");
                    sb.AppendLine(".hidden { display: none; }");
                    sb.AppendLine("</style>");
                    sb.AppendLine("<script>");
                    sb.AppendLine("function applyFilter() {");
                    sb.AppendLine("  var selector = document.getElementById('pawnSelector');");
                    sb.AppendLine("  var modeSelector = document.getElementById('modeSelector');");
                    sb.AppendLine("  var selectedPawn = selector.value;");
                    sb.AppendLine("  var mode = modeSelector.value;");
                    sb.AppendLine("  var msgs = document.getElementsByClassName('msg');");
                    sb.AppendLine("  for (var i = 0; i < msgs.length; i++) {");
                    sb.AppendLine("    var div = msgs[i];");
                    sb.AppendLine("    var pawnName = div.getAttribute('data-pawn');");
                    sb.AppendLine("    div.classList.remove('dimmed', 'hidden');");
                    sb.AppendLine("    if (selectedPawn !== 'All' && pawnName !== selectedPawn) {");
                    sb.AppendLine("      if (mode === 'dim') div.classList.add('dimmed');");
                    sb.AppendLine("      else div.classList.add('hidden');");
                    sb.AppendLine("    }");
                    sb.AppendLine("  }");
                    sb.AppendLine("}");
                    sb.AppendLine("</script>");
                    sb.AppendLine("</head><body>");
                    sb.AppendLine($"<h1>{pageHeader}</h1>");
                    sb.AppendLine($"<h2>{subHeader}</h2>");

                    HashSet<string> names = new HashSet<string>();
                    foreach (var msg in SessionHistory) if (!string.IsNullOrEmpty(msg.PawnName)) names.Add(msg.PawnName);
                    var sortedNames = names.OrderBy(x => x).ToList();

                    sb.AppendLine("<div class='toolbar'>");
                    sb.AppendLine("<label>Filter: </label>");
                    sb.AppendLine("<select id='pawnSelector' onchange='applyFilter()'>");
                    sb.AppendLine("<option value='All'>All</option>");
                    foreach (var n in sortedNames) sb.AppendLine($"<option value='{n}'>{n}</option>");
                    sb.AppendLine("</select>");
                    sb.AppendLine("<label>Mode: </label>");
                    sb.AppendLine("<select id='modeSelector' onchange='applyFilter()'>");
                    sb.AppendLine("<option value='dim'>Dim Others / 暗化他人</option>");
                    sb.AppendLine("<option value='hide'>Hide Others / 隐藏他人</option>");
                    sb.AppendLine("</select>");
                    sb.AppendLine("</div>");

                    sb.AppendLine("<div id='log-container'>");
                    foreach (var msg in SessionHistory)
                    {
                        string nameHex = ColorUtility.ToHtmlStringRGB(msg.NameColor);
                        string nameStyle = $"color: #{nameHex};";
                        if (msg.NameBold) nameStyle += " font-weight: bold;";
                        string contentHtml = msg.Content
                            .Replace("\n", "<br/>")
                            .Replace("<b>", "<strong>")
                            .Replace("</b>", "</strong>")
                            .Replace("<i>", "<em>")
                            .Replace("</i>", "</em>");
                        contentHtml = Regex.Replace(contentHtml, @"<color=#([0-9A-Fa-f]{6})>(.*?)</color>", "<span style='color:#$1'>$2</span>");
                        sb.AppendLine($"<div class='msg' data-pawn='{msg.PawnName}'><span class='name' style='{nameStyle}'>[{msg.PawnName}]</span>: {contentHtml}</div>");
                    }
                    sb.AppendLine("</div>");
                    sb.AppendLine("</body></html>");
                }
                else
                {
                    sb.AppendLine(pageHeader);
                    sb.AppendLine(subHeader);
                    sb.AppendLine(new string('-', 50));
                    foreach (var msg in SessionHistory) { sb.AppendLine($"[{msg.PawnName}]: {msg.OriginalContent}"); }
                }
                File.WriteAllText(path, sb.ToString());
                if (silent) Messages.Message("RTDC_AutoExportMsg".Translate(filename), MessageTypeDefOf.PositiveEvent, false);
                else { Messages.Message("RTDC_ExportSuccess".Translate(filename), MessageTypeDefOf.TaskCompletion, false); Application.OpenURL(basePath); }
            }
            catch (Exception ex) { Log.Error($"[RimTalk DynamicColors] Export failed: {ex}"); if (!silent) Messages.Message("Error exporting log", MessageTypeDefOf.RejectInput, false); }
        }
    }

    public class MainButtonWorker_RimTalkHistory : MainButtonWorker
    {
        public override void Activate()
        {
            Window existing = Find.WindowStack.WindowOfType<RimTalkHistoryWindow>();
            if (existing != null) existing.Close(); else Find.WindowStack.Add(new RimTalkHistoryWindow());
        }
        public override void DoButton(Rect rect)
        {
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(rect)) { Find.WindowStack.Add(new Dialog_ModSettings(DynamicColorMod.settings.Mod)); Event.current.Use(); return; }
            base.DoButton(rect);
        }
    }

    public class RimTalkHistoryWindow : Window
    {
        public RimTalkHistoryWindow() { this.doCloseX = true; this.forcePause = false; this.preventCameraMotion = false; this.draggable = true; this.resizeable = true; this.optionalTitle = "RTDC_HistoryTab".Translate(); }
        public override Vector2 InitialSize => new Vector2(600f, 800f);
        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            float logViewHeight = inRect.height - 380f;
            if (logViewHeight < 200f) logViewHeight = 200f;
            DynamicColorMod.DrawHistoryArea(listing, inRect.width, logViewHeight);
            listing.End();
        }
    }

    public class Dialog_Input : Window
    {
        private string header; private string text; private Action<string> onConfirm;
        public Dialog_Input(string header, string placeholder, string initialText, Action<string> onConfirm) { this.header = header; this.text = initialText; this.onConfirm = onConfirm; this.doCloseX = true; this.absorbInputAroundWindow = true; }
        public override Vector2 InitialSize => new Vector2(400f, 200f);
        public override void DoWindowContents(Rect inRect) { Listing_Standard l = new Listing_Standard(); l.Begin(inRect); l.Label(header); l.Gap(); text = l.TextEntry(text); l.Gap(); if (l.ButtonText("OK")) { onConfirm?.Invoke(text); Close(); } l.End(); }
    }

    public class ColorPickerWindow : Window
    {
        private Color _tempColor;
        private Color _defaultColor;
        private string _previewText;
        private Action<Color> _onCommit;
        private static Texture2D _colorWheelTex;

        public ColorPickerWindow(Color initial, Color defaultColor, string text, Action<Color> onCommit)
        {
            this._tempColor = initial;
            this._defaultColor = defaultColor;
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
            Text.Font = GameFont.Medium; listing.Label("RTDC_SelectColor".Translate()); Text.Font = GameFont.Small; listing.Gap();

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
            Rect previewRect = listing.GetRect(60f); Widgets.DrawBoxSolid(previewRect, new Color(0.1f, 0.1f, 0.1f, 1f)); Widgets.DrawBox(previewRect);
            Color oldColor = GUI.color; GUI.color = _tempColor; Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter; Widgets.Label(previewRect, _previewText); Text.Anchor = TextAnchor.UpperLeft; Text.Font = GameFont.Small; GUI.color = oldColor;

            listing.Gap(20f);
            Rect btnRect = listing.GetRect(30f); float btnW = btnRect.width / 2f - 5f;

            if (Widgets.ButtonText(new Rect(btnRect.x, btnRect.y, btnW, 30f), "OK".Translate())) { _onCommit?.Invoke(_tempColor); Close(); }

            if (Widgets.ButtonText(new Rect(btnRect.x + btnW + 10f, btnRect.y, btnW, 30f), "RTDC_Reset".Translate()))
            {
                _tempColor = _defaultColor;
                GUI.FocusControl(null);
                SoundDefOf.Click.PlayOneShotOnCamera();
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
            if (_colorWheelTex == null || _colorWheelTex.width != (int)rect.width) _colorWheelTex = GenerateColorWheelTexture((int)rect.width);
            GUI.DrawTexture(rect, _colorWheelTex);

            if (Mouse.IsOver(rect) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag))
            {
                Vector2 mousePos = Event.current.mousePosition - rect.position;
                Vector2 center = new Vector2(rect.width / 2f, rect.height / 2f);
                float radius = rect.width / 2f;
                float dist = Vector2.Distance(mousePos, center);

                if (dist <= radius)
                {
                    float angle = Mathf.Atan2(-(mousePos.y - center.y), mousePos.x - center.x) * Mathf.Rad2Deg;
                    if (angle < 0) angle += 360f;
                    float currentAlpha = currentColor.a;
                    currentColor = Color.HSVToRGB(angle / 360f, dist / radius, 1f);
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
                    if (dist > radius)
                    {
                        pixels[y * size + x] = Color.clear;
                    }
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

    [HarmonyPatch(typeof(PawnNameColorUtility), "PawnNameColorOf")]
    public static class Patch_PawnNameColorOf_ContextAware
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn pawn, ref Color __result)
        {
            if (DynamicColorMod.settings == null || !DynamicColorMod.settings.isGlobalEnabled || pawn == null) return true;
            bool inBubbleContext = DynamicColorMod.IsDrawingBubble && DynamicColorMod.lastBubbleFrame == Time.frameCount;
            if (!inBubbleContext) return true;
            bool allow = false;
            if (pawn.RaceProps.Humanlike) { if (pawn.IsPrisoner) allow = DynamicColorMod.settings.enableForPrisoners; else if (pawn.IsSlave) allow = DynamicColorMod.settings.enableForSlaves; else if (pawn.HostileTo(Faction.OfPlayer)) allow = DynamicColorMod.settings.enableForEnemies; else if (pawn.Faction != null && !pawn.Faction.IsPlayer) allow = DynamicColorMod.settings.enableForFriendlies; else if (pawn.Faction == null) allow = DynamicColorMod.settings.enableForGuests; else allow = true; } else { allow = DynamicColorMod.settings.enableForNonHumans; }
            if (!allow) return true;
            string pName = pawn.Name.ToStringShort;
            if (DynamicColorMod.settings.IsManualNameEntry(pName) && DynamicColorMod.settings.GetCombinedCache().TryGetValue(pName, out StyleData manualStyle)) { __result = manualStyle.color; return false; }
            if (DynamicColorMod.settings.autoApplyFavColor && pawn.RaceProps.Humanlike && pawn.story != null && pawn.story.favoriteColor != null) { __result = pawn.story.favoriteColor.color; return false; }
            if (DynamicColorMod.settings.useFactionColor && pawn.Faction != null) { __result = pawn.Faction.Color; return false; }
            if (!pawn.RaceProps.Humanlike && DynamicColorMod.settings.enableForNonHumans) { __result = DynamicColorMod.settings.nonHumanDefaultColor; return false; }
            if (DynamicColorMod.settings.GetCombinedCache().TryGetValue(pName, out StyleData kwStyle)) { __result = kwStyle.color; return false; }
            return true;
        }
    }

    public static class Patch_DrawMessageLog_Manual
    {
        private static FieldInfo _cachedMessagesField;
        private static FieldInfo _pawnNameField;
        private static FieldInfo _pawnInstField;

        public static void Prefix(object __instance)
        {
            DynamicColorMod.IsDrawingChatLog = true;
            DynamicColorMod.lastChatLogFrame = Time.frameCount;

            if (__instance != null)
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
                            var msg = list[i]; if (msg == null) continue; Type t = msg.GetType();
                            if (_pawnNameField == null) _pawnNameField = AccessTools.Field(t, "PawnName") ?? AccessTools.Field(t, "pawnName");
                            string rawName = _pawnNameField.GetValue(msg) as string;
                            if (string.IsNullOrEmpty(rawName)) continue;

                            if (!DynamicColorMod.settings.isGlobalEnabled || !DynamicColorMod.settings.enableRimTalkNameColoring)
                            {
                                if (rawName.StartsWith("<color="))
                                {
                                    string cleanName = Regex.Replace(rawName, @"<color=[^>]*>", "");
                                    cleanName = Regex.Replace(cleanName, @"</color>", "");
                                    _pawnNameField.SetValue(msg, cleanName);
                                }
                                continue;
                            }

                            if (rawName.StartsWith("<color=")) continue;

                            if (_pawnInstField == null) _pawnInstField = AccessTools.Field(t, "PawnInstance") ?? AccessTools.Field(t, "pawnInstance");
                            Pawn p = _pawnInstField?.GetValue(msg) as Pawn;
                            if (p == null) p = DynamicColorMod.FindPawnByName(rawName);

                            Color nameColor = Color.white; bool foundColor = false;
                            if (DynamicColorMod.settings.IsManualNameEntry(rawName) && DynamicColorMod.settings.GetCombinedCache().TryGetValue(rawName, out StyleData manualStyle)) { nameColor = manualStyle.color; foundColor = true; }
                            else if (DynamicColorMod.settings.autoApplyFavColor && p != null && p.RaceProps.Humanlike && p.story != null && p.story.favoriteColor != null) { nameColor = p.story.favoriteColor.color; foundColor = true; }
                            else if (DynamicColorMod.settings.useFactionColor && p != null && p.Faction != null) { nameColor = p.Faction.Color; foundColor = true; }
                            else if (p != null && !p.RaceProps.Humanlike && DynamicColorMod.settings.enableForNonHumans) { nameColor = DynamicColorMod.settings.nonHumanDefaultColor; foundColor = true; }
                            else if (p != null && p.RaceProps.Humanlike && p.HostileTo(Faction.OfPlayer) && DynamicColorMod.settings.enableForEnemies) { nameColor = Color.red; foundColor = true; }
                            else if (DynamicColorMod.settings.GetCombinedCache().TryGetValue(rawName, out StyleData kwStyle)) { nameColor = kwStyle.color; foundColor = true; }

                            if (foundColor) { string hex = ColorUtility.ToHtmlStringRGB(nameColor); _pawnNameField.SetValue(msg, $"<color=#{hex}>{rawName}</color>"); }
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
        }
    }

    public static class Patch_TalkService_CreateInteraction
    {
        private static PropertyInfo _propTalkName;
        private static PropertyInfo _propTalkText;
        private static PropertyInfo _propTalkId;

        public static void Postfix(Pawn pawn, object talk)
        {
            if (pawn == null || talk == null) return;
            if (DynamicColorMod.settings == null || !DynamicColorMod.settings.isGlobalEnabled) return;

            try
            {
                Type t = talk.GetType();
                if (_propTalkName == null) _propTalkName = AccessTools.Property(t, "Name");
                if (_propTalkText == null) _propTalkText = AccessTools.Property(t, "Text");
                if (_propTalkId == null) _propTalkId = AccessTools.Property(t, "Id");

                Guid talkId = Guid.Empty;
                if (_propTalkId != null) talkId = (Guid)_propTalkId.GetValue(talk);
                if (talkId != Guid.Empty && !DynamicColorMod.RecordedTalkIds.Add(talkId)) return;

                string pawnName = _propTalkName?.GetValue(talk) as string ?? pawn.LabelShort;
                string rawDialogue = _propTalkText?.GetValue(talk) as string ?? "";

                if (string.IsNullOrEmpty(pawnName)) pawnName = pawn.LabelShort;
                if (string.IsNullOrEmpty(rawDialogue)) return;

                DynamicColorMod.RecordToSessionHistory(pawn, pawnName, rawDialogue);
            }
            catch (Exception) { }
        }
    }

    public static class Patch_CustomDialogueService_ExecuteDialogue
    {
        private static MethodInfo _methodGetPlayer;

        public static void Postfix(Pawn initiator, Pawn recipient, string message)
        {
            if (initiator == null || string.IsNullOrEmpty(message)) return;
            if (DynamicColorMod.settings == null || !DynamicColorMod.settings.isGlobalEnabled) return;

            try
            {
                if (_methodGetPlayer == null)
                {
                    Type cacheType = AccessTools.TypeByName("RimTalk.Data.Cache");
                    if (cacheType != null)
                        _methodGetPlayer = AccessTools.Method(cacheType, "GetPlayer");
                }

                Pawn playerPawn = _methodGetPlayer?.Invoke(null, null) as Pawn;
                if (playerPawn != initiator) return;

                string pawnName = initiator.LabelShort ?? "Player";
                DynamicColorMod.RecordToSessionHistory(initiator, pawnName, message);
            }
            catch (Exception) { }
        }
    }

    public static class Patch_Bubbles_Bubble_Draw
    {
        public static void Prefix(object __instance)
        {
            DynamicColorMod.CurrentProcessingPawn = null;
            DynamicColorMod.CurrentRecipientPawn = null;
            DynamicColorMod.IsDrawingBubble = false;
            if (__instance == null) return;

            List<Pawn> found = new List<Pawn>();
            foreach (FieldInfo field in DynamicColorMod.BubblePawnFields)
            {
                try
                {
                    Pawn p = field.GetValue(__instance) as Pawn;
                    if (p != null && !found.Contains(p)) found.Add(p);
                }
                catch { }
            }

            if (found.Count > 0)
            {
                DynamicColorMod.CurrentProcessingPawn = found[0];
                if (found.Count > 1) DynamicColorMod.CurrentRecipientPawn = found[1];
            }

            if (DynamicColorMod.CurrentProcessingPawn != null)
            {
                Pawn p = DynamicColorMod.CurrentProcessingPawn;
                bool enabled = true;
                if (p.IsPrisoner && !DynamicColorMod.settings.enableForPrisoners) enabled = false;
                else if (p.IsSlave && !DynamicColorMod.settings.enableForSlaves) enabled = false;
                else if (p.HostileTo(Faction.OfPlayer) && !DynamicColorMod.settings.enableForEnemies) enabled = false;
                else if (p.Faction != null && !p.Faction.IsPlayer && !p.HostileTo(Faction.OfPlayer) && !DynamicColorMod.settings.enableForFriendlies) enabled = false;
                if (!enabled) return;
            }
            DynamicColorMod.IsDrawingBubble = true;
            DynamicColorMod.lastBubbleFrame = Time.frameCount;
        }

        public static void Postfix()
        {
            DynamicColorMod.IsDrawingBubble = false;
            DynamicColorMod.CurrentProcessingPawn = null;
            DynamicColorMod.CurrentRecipientPawn = null;
        }
    }

    public static class Patch_Bubbles_Bubble_GetText
    {
        public static void Postfix(ref string __result)
        {
            if (DynamicColorMod.IsDrawingBubble && DynamicColorMod.settings.enableBubblesSync && !string.IsNullOrEmpty(__result))
            {
                __result = DynamicColorMod.ColorizeString(__result);
            }
        }
    }

    public static class Patch_CommonTranspiler
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var methodLabel = AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new Type[] { typeof(Rect), typeof(string) });
            var methodColorize = AccessTools.Method(typeof(DynamicColorMod), nameof(DynamicColorMod.ColorizeStringForBubbles));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(methodLabel)) yield return new CodeInstruction(OpCodes.Call, methodColorize);
                yield return instruction;
            }
        }
    }

    public class IngestionOutcomeDoer_RemoveRimTalkEffects : IngestionOutcomeDoer
    {
        protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
        {
            if (pawn == null || pawn.health == null) return;
            bool removed = false;
            Hediff rainbow = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(DynamicColorMod.RainbowHediffDefName));
            if (rainbow != null) { pawn.health.RemoveHediff(rainbow); removed = true; }
            Hediff blackWhite = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(DynamicColorMod.BlackWhiteHediffDefName));
            if (blackWhite != null) { pawn.health.RemoveHediff(blackWhite); removed = true; }
            if (removed) Messages.Message($"{pawn.LabelShort} " + "RTDC_DrinkSoup".Translate(), pawn, MessageTypeDefOf.PositiveEvent);
        }
    }

    [HarmonyPatch(typeof(Game), "FinalizeInit")]
    public static class Patch_Game_FinalizeInit
    {
        static void Postfix()
        {
            DynamicColorMod.ClearSessionHistory();
            DynamicColorMod.filterPawnName = "All";
            DynamicColorMod.filterDimMode = true;
            DynamicColorMod.logViewerScrollPos = Vector2.zero;
            if (DynamicColorMod.settings != null) DynamicColorMod.settings.RebuildCache();
            DynamicColorMod.ApplyTabVisibility();
        }
    }
}