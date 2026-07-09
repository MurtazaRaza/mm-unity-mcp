using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.UITK
{
    /// <summary>
    /// Renders a UXML asset offscreen (no Play mode, no scene changes) and saves a PNG.
    /// The Unity Editor is the ground-truth renderer; the agent Reads the PNG from disk.
    /// </summary>
    [McpForUnityTool(
        "uitk_preview",
        Group = "ui",
        RequiresPolling = true,
        PollAction = "status",
        MaxPollSeconds = 90,
        Description = "Render a UI Toolkit UXML asset to a PNG using an offscreen panel in the Unity Editor " +
                      "(edit mode, no scene changes). Returns the PNG file path — read the file to view it. " +
                      "Use 'crop' with a selector ('#name', '.class', 'Button') to zoom into one element.")]
    public static class UitkPreview
    {
        public class Parameters
        {
            [ToolParameter("Project-relative path to the .uxml asset to render, e.g. 'Assets/UI/MainMenu.uxml'", Required = true)]
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

            [ToolParameter("Optional selector to crop the PNG to one element's rect: '#name', '.class', or a type name like 'Button'", Required = false)]
            public string crop { get; set; }

            [ToolParameter("Output folder, project-relative (default 'Screenshots', outside Assets/ to avoid import churn)", Required = false, DefaultValue = "Screenshots")]
            public string output { get; set; }

            [ToolParameter("Output file name (default '<uxml-name>-<timestamp>.png')", Required = false)]
            public string file_name { get; set; }

            [ToolParameter("Also return the image as base64 (for clients without file access)", Required = false, DefaultValue = "false")]
            public bool? include_image { get; set; }

            [ToolParameter("Max edge for the base64 image when include_image is true (default 640)", Required = false, DefaultValue = "640")]
            public int? max_resolution { get; set; }
        }

        public static object HandleCommand(JObject @params)
        {
            try
            {
                var p = new ToolParams(@params);
                string action = (p.Get("action") ?? string.Empty).ToLowerInvariant();
                if (action == "ping")
                    return new SuccessResponse("pong", new { tool = "uitk_preview" });

                object parseError = OffscreenPanel.TryParseRequest(@params, OffscreenPanel.JobKind.Preview, out var request);
                if (parseError != null) return parseError;

                return OffscreenPanel.StartOrPoll(request, BuildResult);
            }
            catch (Exception ex)
            {
                return new ErrorResponse(ex.Message, new { stackTrace = ex.StackTrace });
            }
        }

        private static object BuildResult(OffscreenPanel.PanelSnapshot snapshot)
        {
            var request = snapshot.Request;
            var warnings = new JArray();
            foreach (var w in snapshot.Warnings) warnings.Add(w);

            Texture2D outputTex = snapshot.Texture;
            bool ownsOutputTex = false;
            JObject cropInfo = null;

            try
            {
                // Optional crop to one element's laid-out rect.
                if (!string.IsNullOrEmpty(request.Crop))
                {
                    var matches = UitkQuery.Select(snapshot.Root, request.Crop);
                    if (matches.Count == 0)
                    {
                        warnings.Add($"crop: no element matched '{request.Crop}'; returning the full frame.");
                    }
                    else
                    {
                        var element = matches[0];
                        if (matches.Count > 1)
                            warnings.Add($"crop: {matches.Count} elements matched '{request.Crop}'; " +
                                         $"using the first ({LayoutDiagnostics.ElementPath(element, snapshot.Root)}).");

                        var wb = element.worldBound;
                        int x = Mathf.Clamp(Mathf.FloorToInt(wb.xMin), 0, snapshot.PanelWidth);
                        int y = Mathf.Clamp(Mathf.FloorToInt(wb.yMin), 0, snapshot.PanelHeight);
                        int w = Mathf.Min(Mathf.CeilToInt(wb.xMax) - x, snapshot.PanelWidth - x);
                        int h = Mathf.Min(Mathf.CeilToInt(wb.yMax) - y, snapshot.PanelHeight - y);

                        if (w < 1 || h < 1)
                        {
                            warnings.Add($"crop: element matched by '{request.Crop}' has an empty on-panel rect " +
                                         $"(x:{wb.x:0.#} y:{wb.y:0.#} {wb.width:0.#}x{wb.height:0.#}); returning the full frame.");
                        }
                        else
                        {
                            // worldBound origin is top-left; texture rows start at the bottom.
                            var pixels = snapshot.Texture.GetPixels(x, snapshot.Texture.height - y - h, w, h);
                            var sub = new Texture2D(w, h, TextureFormat.RGBA32, false);
                            sub.SetPixels(pixels);
                            sub.Apply();
                            outputTex = sub;
                            ownsOutputTex = true;
                            cropInfo = new JObject
                            {
                                ["selector"] = request.Crop,
                                ["element"] = LayoutDiagnostics.ElementPath(element, snapshot.Root),
                                ["x"] = x,
                                ["y"] = y,
                                ["width"] = w,
                                ["height"] = h,
                            };
                        }
                    }
                }

                string fileName = string.IsNullOrWhiteSpace(request.FileName)
                    ? $"{Path.GetFileNameWithoutExtension(request.UxmlPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.png"
                    : request.FileName.Trim();
                if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    fileName += ".png";

                Directory.CreateDirectory(request.OutputFolderAbs);
                string fullPath = EnsureUniqueFilePath(
                    Path.Combine(request.OutputFolderAbs, fileName).Replace('\\', '/'));

                byte[] png = outputTex.EncodeToPNG();
                File.WriteAllBytes(fullPath, png);

                string projectRelPath = ScreenshotUtility.ToProjectRelativePath(fullPath);
                if (ScreenshotUtility.IsUnderAssets(projectRelPath))
                    AssetDatabase.ImportAsset(projectRelPath, ImportAssetOptions.ForceSynchronousImport);

                var data = new JObject
                {
                    ["path"] = projectRelPath,
                    ["fullPath"] = fullPath,
                    ["width"] = outputTex.width,
                    ["height"] = outputTex.height,
                    ["panel"] = new JObject { ["width"] = snapshot.PanelWidth, ["height"] = snapshot.PanelHeight },
                    ["hasContent"] = snapshot.HasContent,
                    ["sourceAsset"] = request.UxmlPath,
                    ["context"] = request.Context,
                    ["warnings"] = warnings,
                };
                if (cropInfo != null) data["crop"] = cropInfo;

                if (request.IncludeImage)
                {
                    Texture2D downscaled = null;
                    try
                    {
                        if (outputTex.width > request.MaxResolution || outputTex.height > request.MaxResolution)
                        {
                            downscaled = ScreenshotUtility.DownscaleTexture(outputTex, request.MaxResolution);
                            data["imageBase64"] = Convert.ToBase64String(downscaled.EncodeToPNG());
                            data["imageWidth"] = downscaled.width;
                            data["imageHeight"] = downscaled.height;
                        }
                        else
                        {
                            data["imageBase64"] = Convert.ToBase64String(png);
                            data["imageWidth"] = outputTex.width;
                            data["imageHeight"] = outputTex.height;
                        }
                    }
                    finally
                    {
                        if (downscaled != null) UnityEngine.Object.DestroyImmediate(downscaled);
                    }
                }

                string message = snapshot.HasContent
                    ? $"Rendered '{request.UxmlPath}' to '{projectRelPath}' ({outputTex.width}x{outputTex.height}). Read the PNG to view it."
                    : $"Rendered '{request.UxmlPath}' to '{projectRelPath}' but no visible content was detected — check 'warnings' and run uitk_layout.";

                return new SuccessResponse(message, data);
            }
            finally
            {
                if (ownsOutputTex) UnityEngine.Object.DestroyImmediate(outputTex);
            }
        }

        private static string EnsureUniqueFilePath(string fullPath)
        {
            if (!File.Exists(fullPath)) return fullPath;
            string dir = Path.GetDirectoryName(fullPath);
            string stem = Path.GetFileNameWithoutExtension(fullPath);
            string ext = Path.GetExtension(fullPath);
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir ?? string.Empty, $"{stem}-{i}{ext}").Replace('\\', '/');
                if (!File.Exists(candidate)) return candidate;
            }
            return fullPath;
        }
    }
}
