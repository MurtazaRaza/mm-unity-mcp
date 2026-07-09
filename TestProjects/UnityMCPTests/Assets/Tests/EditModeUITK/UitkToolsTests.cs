using System;
using System.Collections;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.UITK;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.UITK
{
    /// <summary>
    /// End-to-end EditMode tests for uitk_preview / uitk_layout, driving the real polling
    /// contract: first HandleCommand call starts the offscreen panel job, then "status"
    /// calls are issued each editor frame until the job completes.
    /// </summary>
    public class UitkToolsTests
    {
        private const string TempRoot = "Assets/Temp/UitkToolsTests";
        private const string FixtureUxmlPath = TempRoot + "/Fixture.uxml";
        private const string OutputFolder = "Temp/UitkToolsTests";
        private const double TimeoutSeconds = 45.0;

        // Seeded bugs: #overflowing exceeds #box, #zero-height has text but zero height,
        // #a/#b overlap via a negative margin, #offscreen-el sits fully outside the panel.
        private const string FixtureUxml = @"<ui:UXML xmlns:ui=""UnityEngine.UIElements"">
  <ui:VisualElement name=""root"" style=""width: 400px; height: 300px; flex-direction: column; background-color: #202020;"">
    <ui:Label name=""title"" text=""Fixture Title"" />
    <ui:VisualElement name=""box"" style=""width: 200px; height: 80px; background-color: #333366; overflow: visible;"">
      <ui:Label name=""overflowing"" text=""Wide"" style=""width: 300px; height: 20px; flex-shrink: 0;"" />
    </ui:VisualElement>
    <ui:Label name=""zero-height"" text=""Invisible text"" style=""height: 0; flex-shrink: 0;"" />
    <ui:VisualElement name=""a"" style=""width: 100px; height: 40px; background-color: #663333;"" />
    <ui:VisualElement name=""b"" style=""width: 100px; height: 40px; margin-top: -20px; background-color: #336633;"" />
    <ui:VisualElement name=""offscreen-el"" style=""position: absolute; left: -1000px; top: 0; width: 50px; height: 50px;"" />
  </ui:VisualElement>
</ui:UXML>";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureFolder(TempRoot);
            string fullPath = Path.GetFullPath(FixtureUxmlPath);
            File.WriteAllText(fullPath, FixtureUxml);
            AssetDatabase.ImportAsset(FixtureUxmlPath, ImportAssetOptions.ForceUpdate);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
            if (AssetDatabase.IsValidFolder("Assets/Temp")
                && AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Temp" }).Length == 0)
                AssetDatabase.DeleteAsset("Assets/Temp");

            string outputAbs = Path.GetFullPath(OutputFolder);
            if (Directory.Exists(outputAbs))
                Directory.Delete(outputAbs, recursive: true);
        }

        // ---- uitk_preview ----

        [UnityTest]
        public IEnumerator Preview_WritesPngOfRequestedPanelSize()
        {
            JObject result = null;
            yield return RunTool(UitkPreview.HandleCommand, new JObject
            {
                ["path"] = FixtureUxmlPath,
                ["width"] = 640,
                ["height"] = 480,
                ["output"] = OutputFolder,
                ["file_name"] = "full-frame.png",
            }, r => result = r);

            Assert.IsTrue(result.Value<bool>("success"), $"uitk_preview failed: {result}");
            var data = (JObject)result["data"];
            Assert.AreEqual(640, data.Value<int>("width"));
            Assert.AreEqual(480, data.Value<int>("height"));

            string fullPath = data.Value<string>("fullPath");
            Assert.IsTrue(File.Exists(fullPath), $"PNG not written at {fullPath}");
            Assert.Greater(new FileInfo(fullPath).Length, 100, "PNG suspiciously small");

            // Pixel content depends on the (CI) graphics device; only assert when painting happened.
            if (!data.Value<bool>("hasContent"))
                Debug.LogWarning("uitk_preview produced a blank frame in this environment; skipping content assertion.");
        }

        [UnityTest]
        public IEnumerator Preview_Crop_ReturnsElementRect()
        {
            JObject result = null;
            yield return RunTool(UitkPreview.HandleCommand, new JObject
            {
                ["path"] = FixtureUxmlPath,
                ["width"] = 640,
                ["height"] = 480,
                ["crop"] = "#box",
                ["output"] = OutputFolder,
                ["file_name"] = "crop-box.png",
            }, r => result = r);

            Assert.IsTrue(result.Value<bool>("success"), $"uitk_preview failed: {result}");
            var data = (JObject)result["data"];
            var crop = (JObject)data["crop"];
            Assert.IsNotNull(crop, $"crop info missing: {data}");
            Assert.That(crop.Value<int>("width"), Is.EqualTo(200).Within(2), "crop width should match #box");
            Assert.That(crop.Value<int>("height"), Is.EqualTo(80).Within(2), "crop height should match #box");
            Assert.AreEqual(crop.Value<int>("width"), data.Value<int>("width"));
            Assert.AreEqual(crop.Value<int>("height"), data.Value<int>("height"));
        }

        [Test]
        public void Preview_InvalidPath_ReturnsError()
        {
            var result = ToJObject(UitkPreview.HandleCommand(new JObject
            {
                ["path"] = "Assets/DoesNotExist/Nope.uxml",
            }));
            Assert.IsFalse(result.Value<bool>("success"));
        }

        [Test]
        public void Preview_NonUxmlPath_ReturnsError()
        {
            var result = ToJObject(UitkPreview.HandleCommand(new JObject
            {
                ["path"] = "Assets/Foo/Bar.uss",
            }));
            Assert.IsFalse(result.Value<bool>("success"));
        }

        // ---- uitk_layout ----

        [UnityTest]
        public IEnumerator Layout_Diagnostics_CatchSeededBugs()
        {
            JObject result = null;
            yield return RunTool(UitkLayout.HandleCommand, new JObject
            {
                ["path"] = FixtureUxmlPath,
                ["width"] = 640,
                ["height"] = 480,
            }, r => result = r);

            Assert.IsTrue(result.Value<bool>("success"), $"uitk_layout failed: {result}");
            var data = (JObject)result["data"];
            var diagnostics = (JObject)data["diagnostics"];

            Assert.IsTrue(AnyEntryContains(diagnostics["overflows"], "element", "#overflowing"),
                $"expected #overflowing in overflows: {diagnostics["overflows"]}");
            Assert.IsTrue(AnyEntryContains(diagnostics["zeroSize"], "element", "#zero-height"),
                $"expected #zero-height in zeroSize: {diagnostics["zeroSize"]}");
            Assert.IsTrue(diagnostics["overlaps"].Any(e =>
                    e.Value<string>("a").Contains("#a") && e.Value<string>("b").Contains("#b")),
                $"expected #a/#b pair in overlaps: {diagnostics["overlaps"]}");
            Assert.IsTrue(AnyEntryContains(diagnostics["offscreen"], "element", "#offscreen-el"),
                $"expected #offscreen-el in offscreen: {diagnostics["offscreen"]}");
        }

        [UnityTest]
        public IEnumerator Layout_Query_FiltersTree()
        {
            JObject result = null;
            yield return RunTool(UitkLayout.HandleCommand, new JObject
            {
                ["path"] = FixtureUxmlPath,
                ["width"] = 640,
                ["height"] = 480,
                ["query"] = "#box",
            }, r => result = r);

            Assert.IsTrue(result.Value<bool>("success"), $"uitk_layout failed: {result}");
            var tree = (JArray)result["data"]["tree"];
            Assert.AreEqual(1, tree.Count, $"expected exactly one #box match: {tree}");
            Assert.AreEqual("box", tree[0].Value<string>("name"));
            Assert.That(tree[0]["rect"].Value<float>("width"), Is.EqualTo(200f).Within(1f));
            Assert.That(tree[0]["rect"].Value<float>("height"), Is.EqualTo(80f).Within(1f));
        }

        [Test]
        public void Layout_InvalidStyles_ReturnsError()
        {
            var result = ToJObject(UitkLayout.HandleCommand(new JObject
            {
                ["path"] = FixtureUxmlPath,
                ["styles"] = "everything",
            }));
            Assert.IsFalse(result.Value<bool>("success"));
        }

        // ---- Helpers ----

        /// <summary>Drives the polling contract like the Python server does: start, then "status" every frame.</summary>
        private static IEnumerator RunTool(Func<JObject, object> handler, JObject args, Action<JObject> onDone)
        {
            var result = ToJObject(handler(args));
            double deadline = EditorApplication.timeSinceStartup + TimeoutSeconds;
            while (result.Value<string>("_mcp_status") == "pending")
            {
                if (EditorApplication.timeSinceStartup > deadline)
                    Assert.Fail($"Tool did not complete within {TimeoutSeconds}s. Last response: {result}");
                yield return null;
                var poll = (JObject)args.DeepClone();
                poll["action"] = "status";
                result = ToJObject(handler(poll));
            }
            onDone(result);
        }

        private static JObject ToJObject(object response) => JObject.FromObject(response);

        private static bool AnyEntryContains(JToken array, string key, string needle)
        {
            return array is JArray arr && arr.Any(e => (e.Value<string>(key) ?? "").Contains(needle));
        }

        private static void EnsureFolder(string assetFolderPath)
        {
            string[] parts = assetFolderPath.Replace('\\', '/').Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
