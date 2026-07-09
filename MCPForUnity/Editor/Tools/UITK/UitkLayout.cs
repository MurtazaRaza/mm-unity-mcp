using System;
using System.Collections.Generic;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.UITK
{
    /// <summary>
    /// The "DOM inspector" for UI Toolkit: lays out a UXML asset in an offscreen panel and
    /// dumps the element tree with rects, resolved styles, and layout diagnostics
    /// (overflow / zero-size / overlap / offscreen detection) as JSON.
    /// </summary>
    [McpForUnityTool(
        "uitk_layout",
        Group = "ui",
        RequiresPolling = true,
        PollAction = "status",
        MaxPollSeconds = 90,
        Description = "Lay out a UI Toolkit UXML asset in an offscreen editor panel and return the element tree as JSON: " +
                      "per-element rects (worldBound), resolved styles, plus diagnostics for overflowing, zero-size, " +
                      "overlapping, and offscreen elements. Use after edits to verify layout without rendering pixels.")]
    public static class UitkLayout
    {
        public class Parameters
        {
            [ToolParameter("Project-relative path to the .uxml asset to inspect, e.g. 'Assets/UI/MainMenu.uxml'", Required = true)]
            public string path { get; set; }

            [ToolParameter("Optional extra .uss stylesheets to apply to the root (project-relative paths)", Required = false)]
            public string[] uss { get; set; }

            [ToolParameter("Panel context: 'runtime' (default; project runtime theme) or 'editor' (editor skin approximation)", Required = false, DefaultValue = "runtime")]
            public string context { get; set; }

            [ToolParameter("Optional .tss ThemeStyleSheet path; overrides the context default theme", Required = false)]
            public string theme { get; set; }

            [ToolParameter("Panel width in pixels (default 1920)", Required = false, DefaultValue = "1920")]
            public int? width { get; set; }

            [ToolParameter("Panel height in pixels (default 1080)", Required = false, DefaultValue = "1080")]
            public int? height { get; set; }

            [ToolParameter("Optional selector to dump only matching elements (and their subtrees): '#name', '.class', or a type name like 'Button'", Required = false)]
            public string query { get; set; }

            [ToolParameter("Maximum tree depth to serialize (default 20)", Required = false, DefaultValue = "20")]
            public int? max_depth { get; set; }

            [ToolParameter("'summary' (default; layout-relevant resolved styles) or 'full' (everything resolvedStyle offers)", Required = false, DefaultValue = "summary")]
            public string styles { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            try
            {
                var p = new ToolParams(@params);
                string action = (p.Get("action") ?? string.Empty).ToLowerInvariant();
                if (action == "ping")
                    return new SuccessResponse("pong", new { tool = "uitk_layout" });

                object parseError = OffscreenPanel.TryParseRequest(@params, OffscreenPanel.JobKind.Layout, out var request);
                if (parseError != null) return parseError;

                return OffscreenPanel.StartOrPoll(request, BuildResult);
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
            }
        }

        private const int MaxQueryMatches = 20;

        private static object BuildResult(OffscreenPanel.PanelSnapshot snapshot)
        {
            var request = snapshot.Request;
            bool full = request.Styles == "full";
            var warnings = new JArray();
            foreach (var w in snapshot.Warnings) warnings.Add(w);

            var targets = UitkQuery.Select(snapshot.Root, request.Query);
            if (!string.IsNullOrEmpty(request.Query) && targets.Count == 0)
                warnings.Add($"query: no element matched '{request.Query}'. The full-tree diagnostics below still apply.");
            if (targets.Count > MaxQueryMatches)
            {
                warnings.Add($"query: {targets.Count} elements matched '{request.Query}'; returning the first {MaxQueryMatches}.");
                targets = targets.GetRange(0, MaxQueryMatches);
            }

            var tree = new JArray();
            foreach (var target in targets)
                tree.Add(SerializeElement(target, 0, request.MaxDepth, full, snapshot.Root));

            var diagnostics = LayoutDiagnostics.Compute(snapshot.Root, snapshot.PanelWidth, snapshot.PanelHeight);
            int elementCount = CountElements(snapshot.Root);

            var data = new JObject
            {
                ["sourceAsset"] = request.UxmlPath,
                ["context"] = request.Context,
                ["panel"] = new JObject { ["width"] = snapshot.PanelWidth, ["height"] = snapshot.PanelHeight },
                ["elementCount"] = elementCount,
                ["tree"] = tree,
                ["diagnostics"] = diagnostics,
                ["warnings"] = warnings,
            };

            var counts = (JObject)diagnostics["counts"];
            string message =
                $"Layout of '{request.UxmlPath}': {elementCount} elements. Diagnostics — " +
                $"overflows: {counts["overflows"]}, zeroSize: {counts["zeroSize"]}, " +
                $"overlaps: {counts["overlaps"]}, offscreen: {counts["offscreen"]}.";

            return new SuccessResponse(message, data);
        }

        // ---- Tree serialization ----

        private static JObject SerializeElement(VisualElement element, int depth, int maxDepth, bool full, VisualElement root)
        {
            var result = new JObject
            {
                ["type"] = element.GetType().Name,
            };
            if (!string.IsNullOrEmpty(element.name))
                result["name"] = element.name;

            var classes = new JArray();
            foreach (var c in element.GetClasses()) classes.Add(c);
            if (classes.Count > 0)
                result["classes"] = classes;

            if (element is TextElement textElement && !string.IsNullOrEmpty(textElement.text))
                result["text"] = textElement.text;

            // Always emit the rect, including zeros — zero-size is a signal, not noise.
            result["rect"] = UitkJson.Rect(element.worldBound);

            result["resolvedStyle"] = full ? SerializeFullStyle(element) : SerializeSummaryStyle(element);

            int childCount = element.hierarchy.childCount;
            if (childCount > 0)
            {
                if (depth < maxDepth)
                {
                    var children = new JArray();
                    foreach (var child in element.hierarchy.Children())
                        children.Add(SerializeElement(child, depth + 1, maxDepth, full, root));
                    result["children"] = children;
                }
                else
                {
                    result["childCount"] = childCount;
                    result["truncated"] = true;
                }
            }

            return result;
        }

        private static JObject SerializeSummaryStyle(VisualElement element)
        {
            var rs = element.resolvedStyle;
            return new JObject
            {
                ["position"] = rs.position.ToString(),
                ["flexDirection"] = rs.flexDirection.ToString(),
                ["flexGrow"] = UitkJson.F(rs.flexGrow),
                ["flexShrink"] = UitkJson.F(rs.flexShrink),
                ["flexBasis"] = rs.flexBasis.ToString(),
                ["alignItems"] = rs.alignItems.ToString(),
                ["alignSelf"] = rs.alignSelf.ToString(),
                ["alignContent"] = rs.alignContent.ToString(),
                ["justifyContent"] = rs.justifyContent.ToString(),
                ["margin"] = Trbl(rs.marginTop, rs.marginRight, rs.marginBottom, rs.marginLeft),
                ["padding"] = Trbl(rs.paddingTop, rs.paddingRight, rs.paddingBottom, rs.paddingLeft),
                ["borderWidth"] = Trbl(rs.borderTopWidth, rs.borderRightWidth, rs.borderBottomWidth, rs.borderLeftWidth),
                ["width"] = UitkJson.F(rs.width),
                ["height"] = UitkJson.F(rs.height),
                ["minWidth"] = rs.minWidth.ToString(),
                ["maxWidth"] = rs.maxWidth.ToString(),
                ["minHeight"] = rs.minHeight.ToString(),
                ["maxHeight"] = rs.maxHeight.ToString(),
                ["display"] = rs.display.ToString(),
                ["visibility"] = rs.visibility.ToString(),
                ["opacity"] = UitkJson.F(rs.opacity),
                ["overflow"] = LayoutDiagnostics.TryGetOverflow(element) ?? "unknown",
                ["fontSize"] = UitkJson.F(rs.fontSize),
                ["unityFontDefinition"] = FormatFontDefinition(rs.unityFontDefinition),
                ["color"] = UitkJson.ColorHex(rs.color),
                ["backgroundColor"] = UitkJson.ColorHex(rs.backgroundColor),
            };
        }

        private static JObject SerializeFullStyle(VisualElement element)
        {
            var result = new JObject();
            var rs = element.resolvedStyle;
            foreach (var property in typeof(IResolvedStyle).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    result[property.Name] = FormatStyleValue(property.GetValue(rs));
                }
                catch (Exception)
                {
                    // Skip properties that throw on this Unity version.
                }
            }
            // overflow is not part of IResolvedStyle on all versions; add it explicitly.
            if (result["overflow"] == null)
                result["overflow"] = LayoutDiagnostics.TryGetOverflow(element) ?? "unknown";
            return result;
        }

        private static JToken FormatStyleValue(object value)
        {
            switch (value)
            {
                case null:
                    return JValue.CreateNull();
                case float f:
                    return UitkJson.F(f);
                case double d:
                    return UitkJson.F((float)d);
                case int i:
                    return i;
                case bool b:
                    return b;
                case Color color:
                    return UitkJson.ColorHex(color);
                case FontDefinition fontDefinition:
                    return FormatFontDefinition(fontDefinition);
                case UnityEngine.Object obj:
                    return obj != null ? obj.name : null;
                default:
                    return value.ToString();
            }
        }

        private static string FormatFontDefinition(FontDefinition fontDefinition)
        {
            try
            {
                if (fontDefinition.fontAsset != null) return fontDefinition.fontAsset.name;
                if (fontDefinition.font != null) return fontDefinition.font.name;
            }
            catch (Exception)
            {
                // FontDefinition accessors can throw when uninitialized on some versions.
            }
            return "none";
        }

        private static JObject Trbl(float top, float right, float bottom, float left)
        {
            return new JObject
            {
                ["top"] = UitkJson.F(top),
                ["right"] = UitkJson.F(right),
                ["bottom"] = UitkJson.F(bottom),
                ["left"] = UitkJson.F(left),
            };
        }

        private static int CountElements(VisualElement element)
        {
            int count = 1;
            foreach (var child in element.hierarchy.Children())
                count += CountElements(child);
            return count;
        }
    }
}
