using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.UITK
{
    /// <summary>
    /// Computes layout diagnostics over a laid-out visual tree so agents don't have to do
    /// geometry from raw JSON: parent overflows, zero-size elements with content, overlapping
    /// siblings, and elements fully outside the panel.
    /// </summary>
    internal static class LayoutDiagnostics
    {
        private const float Epsilon = 0.5f;
        private const int MaxEntriesPerCategory = 50;

        internal static JObject Compute(VisualElement root, float panelWidth, float panelHeight)
        {
            var overflows = new JArray();
            var zeroSize = new JArray();
            var overlaps = new JArray();
            var offscreen = new JArray();
            int overflowCount = 0, zeroSizeCount = 0, overlapCount = 0, offscreenCount = 0;

            Walk(root, root, panelWidth, panelHeight,
                overflows, zeroSize, overlaps, offscreen,
                ref overflowCount, ref zeroSizeCount, ref overlapCount, ref offscreenCount);

            bool truncated = overflowCount > overflows.Count || zeroSizeCount > zeroSize.Count
                          || overlapCount > overlaps.Count || offscreenCount > offscreen.Count;

            return new JObject
            {
                ["overflows"] = overflows,
                ["zeroSize"] = zeroSize,
                ["overlaps"] = overlaps,
                ["offscreen"] = offscreen,
                ["counts"] = new JObject
                {
                    ["overflows"] = overflowCount,
                    ["zeroSize"] = zeroSizeCount,
                    ["overlaps"] = overlapCount,
                    ["offscreen"] = offscreenCount,
                },
                ["truncated"] = truncated,
            };
        }

        private static void Walk(VisualElement element, VisualElement root,
            float panelWidth, float panelHeight,
            JArray overflows, JArray zeroSize, JArray overlaps, JArray offscreen,
            ref int overflowCount, ref int zeroSizeCount, ref int overlapCount, ref int offscreenCount)
        {
            if (element.resolvedStyle.display == DisplayStyle.None)
                return;

            var rect = element.worldBound;

            // Zero-size elements that actually carry content.
            bool hasText = element is TextElement te && !string.IsNullOrEmpty(te.text);
            if ((rect.width < Epsilon || rect.height < Epsilon)
                && (hasText || element.hierarchy.childCount > 0))
            {
                zeroSizeCount++;
                if (zeroSize.Count < MaxEntriesPerCategory)
                {
                    var entry = new JObject
                    {
                        ["element"] = ElementPath(element, root),
                        ["rect"] = UitkJson.Rect(rect),
                        ["childCount"] = element.hierarchy.childCount,
                    };
                    if (hasText) entry["text"] = ((TextElement)element).text;
                    zeroSize.Add(entry);
                }
            }

            // Fully outside the panel.
            if (element != root &&
                (rect.xMax <= 0f || rect.yMax <= 0f || rect.xMin >= panelWidth || rect.yMin >= panelHeight))
            {
                offscreenCount++;
                if (offscreen.Count < MaxEntriesPerCategory)
                {
                    offscreen.Add(new JObject
                    {
                        ["element"] = ElementPath(element, root),
                        ["rect"] = UitkJson.Rect(rect),
                        ["panel"] = new JObject { ["width"] = panelWidth, ["height"] = panelHeight },
                    });
                }
            }

            // Children overflowing this element's bounds.
            var children = new List<VisualElement>();
            foreach (var child in element.hierarchy.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None) continue;
                children.Add(child);

                var childRect = child.worldBound;
                float overLeft = rect.xMin - childRect.xMin;
                float overTop = rect.yMin - childRect.yMin;
                float overRight = childRect.xMax - rect.xMax;
                float overBottom = childRect.yMax - rect.yMax;

                if (overLeft > Epsilon || overTop > Epsilon || overRight > Epsilon || overBottom > Epsilon)
                {
                    overflowCount++;
                    if (overflows.Count < MaxEntriesPerCategory)
                    {
                        var amount = new JObject();
                        if (overLeft > Epsilon) amount["left"] = UitkJson.F(overLeft);
                        if (overTop > Epsilon) amount["top"] = UitkJson.F(overTop);
                        if (overRight > Epsilon) amount["right"] = UitkJson.F(overRight);
                        if (overBottom > Epsilon) amount["bottom"] = UitkJson.F(overBottom);

                        overflows.Add(new JObject
                        {
                            ["element"] = ElementPath(child, root),
                            ["rect"] = UitkJson.Rect(childRect),
                            ["position"] = child.resolvedStyle.position.ToString(),
                            ["parent"] = ElementPath(element, root),
                            ["parentRect"] = UitkJson.Rect(rect),
                            ["parentOverflow"] = TryGetOverflow(element) ?? "unknown",
                            ["amount"] = amount,
                        });
                    }
                }
            }

            // Overlapping sibling pairs (absolute-positioned elements usually overlap by design).
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].resolvedStyle.position == Position.Absolute) continue;
                var rectA = children[i].worldBound;
                for (int j = i + 1; j < children.Count; j++)
                {
                    if (children[j].resolvedStyle.position == Position.Absolute) continue;
                    var rectB = children[j].worldBound;

                    float ix = Mathf.Min(rectA.xMax, rectB.xMax) - Mathf.Max(rectA.xMin, rectB.xMin);
                    float iy = Mathf.Min(rectA.yMax, rectB.yMax) - Mathf.Max(rectA.yMin, rectB.yMin);
                    if (ix > Epsilon && iy > Epsilon)
                    {
                        overlapCount++;
                        if (overlaps.Count < MaxEntriesPerCategory)
                        {
                            overlaps.Add(new JObject
                            {
                                ["a"] = ElementPath(children[i], root),
                                ["b"] = ElementPath(children[j], root),
                                ["rectA"] = UitkJson.Rect(rectA),
                                ["rectB"] = UitkJson.Rect(rectB),
                                ["overlap"] = new JObject
                                {
                                    ["width"] = UitkJson.F(ix),
                                    ["height"] = UitkJson.F(iy),
                                },
                            });
                        }
                    }
                }
            }

            foreach (var child in children)
            {
                Walk(child, root, panelWidth, panelHeight,
                    overflows, zeroSize, overlaps, offscreen,
                    ref overflowCount, ref zeroSizeCount, ref overlapCount, ref offscreenCount);
            }
        }

        // ---- Shared element helpers ----

        /// <summary>Readable path like "#root &gt; #menu &gt; Button" for identifying elements in results.</summary>
        internal static string ElementPath(VisualElement element, VisualElement stopAt)
        {
            var parts = new List<string>();
            var current = element;
            while (current != null && parts.Count < 12)
            {
                parts.Add(string.IsNullOrEmpty(current.name) ? current.GetType().Name : "#" + current.name);
                if (current == stopAt) break;
                current = current.hierarchy.parent;
            }
            parts.Reverse();
            return string.Join(" > ", parts);
        }

        private static readonly PropertyInfo s_overflowProperty = typeof(IResolvedStyle).GetProperty("overflow");

        /// <summary>
        /// The resolved overflow value when this Unity version exposes it on IResolvedStyle;
        /// otherwise falls back to the inline style (USS-set values are then unknowable → null).
        /// </summary>
        internal static string TryGetOverflow(VisualElement element)
        {
            if (s_overflowProperty != null)
            {
                try { return s_overflowProperty.GetValue(element.resolvedStyle)?.ToString(); }
                catch (Exception) { /* fall through */ }
            }

            var inline = element.style.overflow;
            if (inline.keyword == StyleKeyword.Undefined || inline.keyword == StyleKeyword.Null)
                return null;
            return inline.value.ToString();
        }
    }

    /// <summary>JSON formatting helpers shared by the UITK tools.</summary>
    internal static class UitkJson
    {
        /// <summary>Rounds to 2 decimals; NaN/Infinity become null (invalid in JSON).</summary>
        internal static JToken F(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return JValue.CreateNull();
            return Math.Round(value, 2);
        }

        internal static JObject Rect(UnityEngine.Rect rect)
        {
            return new JObject
            {
                ["x"] = F(rect.x),
                ["y"] = F(rect.y),
                ["width"] = F(rect.width),
                ["height"] = F(rect.height),
            };
        }

        internal static string ColorHex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGBA(color);
        }
    }
}
