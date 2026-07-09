using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.UITK
{
    /// <summary>
    /// Shared offscreen render/layout substrate for uitk_preview and uitk_layout.
    ///
    /// Each request gets a hidden GameObject + UIDocument + an in-memory PanelSettings and
    /// RenderTexture (nothing is saved as an asset). The tools use the package polling
    /// contract (RequiresPolling): the first call creates the panel and returns a
    /// PendingResponse; the server's "status" polls complete once the panel has produced a
    /// layout pass (and, for previews, non-blank pixels) or an internal timeout elapses.
    /// </summary>
    internal static class OffscreenPanel
    {
        internal enum JobKind { Preview, Layout }

        internal sealed class Request
        {
            public JobKind Kind;
            public string UxmlPath;
            public string[] ExtraUss;
            public string Context;          // "runtime" | "editor"
            public string ThemePath;        // optional explicit theme override
            public int Width;
            public int Height;

            // Preview-only
            public string Crop;
            public string OutputFolderAbs;  // resolved absolute folder
            public string FileName;
            public bool IncludeImage;
            public int MaxResolution;

            // Layout-only
            public string Query;
            public int MaxDepth;
            public string Styles;           // "summary" | "full"

            public string CacheKey => string.Join("|",
                Kind, UxmlPath,
                ExtraUss == null ? string.Empty : string.Join(",", ExtraUss),
                Context, ThemePath, Width, Height,
                Crop, OutputFolderAbs, FileName, IncludeImage, MaxResolution,
                Query, MaxDepth, Styles);
        }

        /// <summary>Everything a tool needs to build its final response. Valid only inside the build callback.</summary>
        internal sealed class PanelSnapshot
        {
            public Request Request;
            public VisualElement Root;
            public int PanelWidth;
            public int PanelHeight;
            public Texture2D Texture;       // preview only; owned and destroyed by OffscreenPanel
            public bool HasContent;
            public List<string> Warnings;
            public double ElapsedSeconds;
        }

        private sealed class Job
        {
            public Request Request;
            public GameObject Go;
            public UIDocument Document;
            public PanelSettings Panel;
            public RenderTexture Rt;
            public int Ticks;
            public double StartTime;
            public readonly List<string> Warnings = new List<string>();
        }

        // ---- Internal panel driving ----
        //
        // The offscreen UIDocument's panel is a RuntimePanel with no visible GUIView/EditorWindow
        // behind it. EditorApplication.QueuePlayerLoopUpdate()/RepaintAllViews() only reach panels
        // owned by an actual view, so they never advance layout or painting for this panel. In Play
        // mode the player loop drives RuntimePanel.Update()/Render() directly; in edit mode (no Play
        // mode running) nothing does, so we call those internal methods via reflection ourselves.
        private static readonly MethodInfo s_panelUpdate = ResolvePanelMethod("Update");
        private static readonly MethodInfo s_panelRender = ResolvePanelMethod("Render");
        private static readonly PropertyInfo s_panelSettingsPanelProp =
            typeof(PanelSettings).GetProperty("panel", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static MethodInfo ResolvePanelMethod(string name)
        {
            var runtimePanelType = typeof(PanelSettings).Assembly.GetType("UnityEngine.UIElements.BaseRuntimePanel");
            return runtimePanelType?.GetMethod(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        }

        private static void DrivePanel(PanelSettings panelSettings)
        {
            var panel = s_panelSettingsPanelProp?.GetValue(panelSettings);
            if (panel == null) return;
            s_panelUpdate?.Invoke(panel, null);
            s_panelRender?.Invoke(panel, null);
        }

        private const double LayoutTimeoutSeconds = 20.0;
        private const double ContentTimeoutSeconds = 12.0;
        private const double JobExpirySeconds = 300.0;
        private const double PollIntervalSeconds = 0.5;
        private const int MaxWarnings = 20;

        private static readonly Dictionary<string, Job> s_jobs = new Dictionary<string, Job>();
        private static bool s_hooked;

        static OffscreenPanel()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            EditorApplication.quitting += DisposeAll;
        }

        // ---- Parameter parsing (shared by both tools) ----

        /// <summary>Parses and validates params. Returns an ErrorResponse on failure, null on success.</summary>
        internal static object TryParseRequest(JObject @params, JobKind kind, out Request req)
        {
            req = null;
            var p = new ToolParams(@params);

            string path = AssetPathUtility.SanitizeAssetPath(p.Get("path"));
            if (string.IsNullOrEmpty(path))
                return new ErrorResponse("'path' parameter is required (project-relative UXML asset path).");
            if (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
                return new ErrorResponse($"'path' must point to a .uxml asset, got '{path}'.");

            string[] uss = p.GetStringArray("uss");
            if (uss != null)
            {
                for (int i = 0; i < uss.Length; i++)
                {
                    uss[i] = AssetPathUtility.SanitizeAssetPath(uss[i]);
                    if (string.IsNullOrEmpty(uss[i]) || !uss[i].EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
                        return new ErrorResponse($"'uss' entries must be project-relative .uss paths, got '{p.GetStringArray("uss")[i]}'.");
                }
            }

            string context = (p.Get("context") ?? "runtime").Trim().ToLowerInvariant();
            if (context != "runtime" && context != "editor")
                return new ErrorResponse($"'context' must be 'runtime' or 'editor', got '{context}'.");

            string theme = p.Get("theme");
            if (!string.IsNullOrEmpty(theme))
            {
                theme = AssetPathUtility.SanitizeAssetPath(theme);
                if (theme == null)
                    return new ErrorResponse("Invalid 'theme' path.");
            }

            int width = Mathf.Clamp(p.GetInt("width") ?? 1920, 16, 4096);
            int height = Mathf.Clamp(p.GetInt("height") ?? 1080, 16, 4096);

            req = new Request
            {
                Kind = kind,
                UxmlPath = path,
                ExtraUss = uss,
                Context = context,
                ThemePath = theme,
                Width = width,
                Height = height,
            };

            if (kind == JobKind.Preview)
            {
                req.Crop = p.Get("crop");
                req.FileName = p.Get("file_name");
                req.IncludeImage = p.GetBool("include_image");
                req.MaxResolution = Mathf.Clamp(p.GetInt("max_resolution") ?? 640, 64, 4096);

                string folderSpec = p.Get("output");
                if (string.IsNullOrWhiteSpace(folderSpec)) folderSpec = "Screenshots";
                try
                {
                    req.OutputFolderAbs = MCPForUnity.Runtime.Helpers.ScreenshotUtility.ResolveFolderAbsolute(folderSpec);
                }
                catch (InvalidOperationException ex)
                {
                    return new ErrorResponse(ex.Message);
                }
            }
            else
            {
                req.Query = p.Get("query");
                req.MaxDepth = Mathf.Clamp(p.GetInt("max_depth") ?? 20, 1, 100);
                req.Styles = (p.Get("styles") ?? "summary").Trim().ToLowerInvariant();
                if (req.Styles != "summary" && req.Styles != "full")
                    return new ErrorResponse($"'styles' must be 'summary' or 'full', got '{req.Styles}'.");
            }

            return null;
        }

        // ---- Job lifecycle ----

        /// <summary>
        /// Entry point for both the initial call and "status" polls: starts a job if none
        /// exists for this request, otherwise polls it. Calls <paramref name="buildResult"/>
        /// exactly once, when the panel is ready, then disposes the job.
        /// </summary>
        internal static object StartOrPoll(Request req, Func<PanelSnapshot, object> buildResult)
        {
            if (!s_jobs.TryGetValue(req.CacheKey, out var job))
            {
                object error = StartJob(req, out job);
                if (error != null) return error;
                Pump(job);
                return Pending(job, "Offscreen panel created; waiting for layout and paint.");
            }

            return Poll(job, buildResult);
        }

        private static object StartJob(Request req, out Job job)
        {
            job = null;
            var warnings = new List<string>();

            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(req.UxmlPath);
            if (vta == null)
                return new ErrorResponse(
                    $"Could not load VisualTreeAsset at '{req.UxmlPath}'. Check the path and the console for UXML import errors.");

            var extraSheets = new List<StyleSheet>();
            if (req.ExtraUss != null)
            {
                foreach (var ussPath in req.ExtraUss)
                {
                    var ss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
                    if (ss == null)
                        return new ErrorResponse($"Could not load StyleSheet at '{ussPath}'.");
                    extraSheets.Add(ss);
                }
            }

            ThemeStyleSheet theme = null;
            if (!string.IsNullOrEmpty(req.ThemePath))
            {
                theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(req.ThemePath);
                if (theme == null)
                    return new ErrorResponse($"Could not load ThemeStyleSheet at '{req.ThemePath}' (expected a .tss asset).");
            }
            else if (req.Context == "runtime")
            {
                theme = FindDefaultRuntimeTheme();
                if (theme == null)
                    warnings.Add("No ThemeStyleSheet found in the project; rendering without a theme. " +
                                 "Text and built-in controls may not render correctly. Create one via " +
                                 "Assets > Create > UI Toolkit > Default Runtime Theme, or pass 'theme'.");
            }

            RenderTexture rt = null;
            PanelSettings ps = null;
            GameObject go = null;
            try
            {
                rt = new RenderTexture(req.Width, req.Height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "MCP_UITK_Offscreen_RT"
                };
                rt.Create();
                // Clear to transparent so stale GPU memory never reads as content.
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = prevActive;

                ps = ScriptableObject.CreateInstance<PanelSettings>();
                ps.hideFlags = HideFlags.HideAndDontSave;
                ps.name = "MCP_UITK_Offscreen_PanelSettings";
                ps.scaleMode = PanelScaleMode.ConstantPixelSize;
                ps.scale = 1f;
                ps.clearColor = true;
                ps.colorClearValue = Color.clear;
                if (theme != null) ps.themeStyleSheet = theme;
                ps.targetTexture = rt;

                go = new GameObject("__MCP_UITK_Offscreen__") { hideFlags = HideFlags.HideAndDontSave };
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = ps;
                doc.visualTreeAsset = vta;

                var root = doc.rootVisualElement;
                if (root == null)
                    throw new InvalidOperationException(
                        "UIDocument did not produce a root visual element (UXML failed to instantiate?). Check the console.");

                if (req.Context == "editor")
                {
                    var editorSheet = GetEditorDefaultStyleSheet(warnings);
                    if (editorSheet != null)
                        root.styleSheets.Add(editorSheet);
                }

                foreach (var ss in extraSheets)
                    root.styleSheets.Add(ss);

                job = new Job
                {
                    Request = req,
                    Go = go,
                    Document = doc,
                    Panel = ps,
                    Rt = rt,
                    StartTime = EditorApplication.timeSinceStartup,
                };
                job.Warnings.AddRange(warnings);

                s_jobs[req.CacheKey] = job;
                Hook();
                return null;
            }
            catch (Exception ex)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (ps != null) UnityEngine.Object.DestroyImmediate(ps);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                return new ErrorResponse($"Failed to set up offscreen panel: {ex.Message}");
            }
        }

        private static object Poll(Job job, Func<PanelSnapshot, object> buildResult)
        {
            Pump(job);

            var root = job.Document != null ? job.Document.rootVisualElement : null;
            if (root == null)
            {
                Dispose(job);
                return new ErrorResponse("Offscreen panel was destroyed before completing (domain reload or editor state change). Retry the call.");
            }

            double elapsed = EditorApplication.timeSinceStartup - job.StartTime;
            var rootLayout = root.layout;
            bool layoutReady = job.Ticks >= 2
                               && !float.IsNaN(rootLayout.width)
                               && rootLayout.width > 0f;

            if (!layoutReady)
            {
                if (elapsed > LayoutTimeoutSeconds)
                {
                    var warnings = new List<string>(job.Warnings);
                    Dispose(job);
                    return new ErrorResponse(
                        $"Panel never completed a layout pass within {LayoutTimeoutSeconds:0}s.",
                        new { warnings });
                }
                return Pending(job, "Waiting for the panel's first layout pass.");
            }

            if (job.Request.Kind == JobKind.Layout)
            {
                return Finish(job, root, null, false, elapsed, buildResult);
            }

            // Preview: wait for non-blank pixels (or give up and return the blank frame with a warning).
            var tex = ReadRenderTexture(job.Rt);
            bool hasContent = HasAnyContent(tex);
            if (!hasContent && elapsed < ContentTimeoutSeconds)
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return Pending(job, "Layout complete; waiting for the panel to paint.");
            }

            if (!hasContent)
            {
                job.Warnings.Add($"No visible pixels after {elapsed:0.0}s. The UXML may render nothing " +
                                 "(empty tree, zero-size elements, missing theme) — check the layout with uitk_layout.");
            }
            return Finish(job, root, tex, hasContent, elapsed, buildResult);
        }

        private static object Finish(Job job, VisualElement root, Texture2D tex, bool hasContent,
            double elapsed, Func<PanelSnapshot, object> buildResult)
        {
            try
            {
                var snapshot = new PanelSnapshot
                {
                    Request = job.Request,
                    Root = root,
                    PanelWidth = job.Rt.width,
                    PanelHeight = job.Rt.height,
                    Texture = tex,
                    HasContent = hasContent,
                    Warnings = new List<string>(job.Warnings),
                    ElapsedSeconds = elapsed,
                };
                return buildResult(snapshot);
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"Failed to build result: {ex.Message}", new { stackTrace = ex.StackTrace });
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                Dispose(job);
            }
        }

        private static PendingResponse Pending(Job job, string message)
        {
            return new PendingResponse(message, PollIntervalSeconds, new { ticks = job.Ticks });
        }

        // ---- Editor pumping ----

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            EditorApplication.update += OnEditorUpdate;
            Application.logMessageReceived += OnLog;
        }

        private static void Unhook()
        {
            if (!s_hooked) return;
            s_hooked = false;
            EditorApplication.update -= OnEditorUpdate;
            Application.logMessageReceived -= OnLog;
        }

        private static void OnEditorUpdate()
        {
            if (s_jobs.Count == 0)
            {
                Unhook();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            foreach (var job in s_jobs.Values.ToList())
            {
                if (now - job.StartTime > JobExpirySeconds)
                {
                    Dispose(job);
                    continue;
                }
                job.Ticks++;
                job.Document?.rootVisualElement?.MarkDirtyRepaint();
                DrivePanel(job.Panel);
            }
        }

        private static void Pump(Job job)
        {
            job.Document?.rootVisualElement?.MarkDirtyRepaint();
            DrivePanel(job.Panel);
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            bool relevant = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            if (!relevant && type == LogType.Warning)
            {
                relevant = ContainsAny(condition,
                    "uss", "uxml", "uielements", "ui toolkit", "stylesheet", "theme", "font", "panel");
            }
            if (!relevant) return;

            foreach (var job in s_jobs.Values)
            {
                if (job.Warnings.Count < MaxWarnings)
                    job.Warnings.Add($"[{type}] {condition}");
                else if (job.Warnings.Count == MaxWarnings)
                    job.Warnings.Add("(more console messages truncated)");
            }
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            foreach (var n in needles)
            {
                if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // ---- Cleanup ----

        private static void Dispose(Job job)
        {
            s_jobs.Remove(job.Request.CacheKey);
            if (job.Panel != null) job.Panel.targetTexture = null;
            if (job.Go != null) UnityEngine.Object.DestroyImmediate(job.Go);
            if (job.Panel != null) UnityEngine.Object.DestroyImmediate(job.Panel);
            if (job.Rt != null)
            {
                job.Rt.Release();
                UnityEngine.Object.DestroyImmediate(job.Rt);
            }
            if (s_jobs.Count == 0) Unhook();
        }

        private static void DisposeAll()
        {
            foreach (var job in s_jobs.Values.ToList())
                Dispose(job);
        }

        // ---- Pixels ----

        private static Texture2D ReadRenderTexture(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        private static bool HasAnyContent(Texture2D tex)
        {
            var pixels = tex.GetPixels32();
            int step = Mathf.Max(1, pixels.Length / 2000);
            for (int i = 0; i < pixels.Length; i += step)
            {
                if (pixels[i].a > 0) return true;
            }
            return false;
        }

        // ---- Theme resolution ----

        private const string DefaultRuntimeThemePath = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss";

        private static ThemeStyleSheet FindDefaultRuntimeTheme()
        {
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(DefaultRuntimeThemePath);
            if (theme != null) return theme;

            string[] guids = AssetDatabase.FindAssets("t:ThemeStyleSheet");
            foreach (var guid in guids)
            {
                theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (theme != null) return theme;
            }
            return null;
        }

        private static StyleSheet GetEditorDefaultStyleSheet(List<string> warnings)
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.UIElements.UIElementsEditorUtility");
                string methodName = EditorGUIUtility.isProSkin ? "GetCommonDarkStyleSheet" : "GetCommonLightStyleSheet";
                var method = type?.GetMethod(methodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method?.Invoke(null, null) is StyleSheet sheet && sheet != null)
                    return sheet;
            }
            catch (Exception)
            {
                // fall through to warning
            }

            warnings.Add("Could not resolve the editor default stylesheet for context 'editor'; " +
                         "the preview approximates EditorWindow styling less closely on this Unity version.");
            return null;
        }
    }

    /// <summary>
    /// Minimal UQuery-style selector used by 'crop' and 'query' parameters:
    /// "#name", ".class", a bare element name, or a VisualElement type name (e.g. "Button").
    /// </summary>
    internal static class UitkQuery
    {
        internal static List<VisualElement> Select(VisualElement root, string selector)
        {
            var results = new List<VisualElement>();
            if (root == null) return results;
            if (string.IsNullOrWhiteSpace(selector))
            {
                results.Add(root);
                return results;
            }

            selector = selector.Trim();
            if (selector.StartsWith("#", StringComparison.Ordinal))
                return root.Query(name: selector.Substring(1)).ToList();
            if (selector.StartsWith(".", StringComparison.Ordinal))
                return root.Query(className: selector.Substring(1)).ToList();

            var byName = root.Query(name: selector).ToList();
            if (byName.Count > 0) return byName;

            Collect(root, e => e.GetType().Name == selector, results);
            return results;
        }

        private static void Collect(VisualElement element, Func<VisualElement, bool> predicate, List<VisualElement> into)
        {
            if (predicate(element)) into.Add(element);
            foreach (var child in element.hierarchy.Children())
                Collect(child, predicate, into);
        }
    }
}
