using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PLATEAU.CityInfo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// インポート結果が「測れる中身になっているか」を数で確かめる（docs/m0-plan.md §3・§4.2）。
    ///
    /// Hierarchyを目で見て判断すると、LOD0（底面ポリゴンだけ）が入っているのに
    /// 「都市が入った」と誤認しやすい。粒度・LOD・三角形数・描画対象数を出して、
    /// **インポート設定がM0の測定条件に合っているかを数字で確定させる**。
    ///
    /// バッチモードから（Editorを閉じてから実行すること。プロジェクトは同時に開けない）:
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M0SceneStats.DumpBenchmarkScene
    /// </summary>
    public static class M0SceneStats
    {
        private const string BenchmarkScenePath = "Assets/Scenes/M0Benchmark.unity";
        private const string OutputDir = "docs/m0";

        [MenuItem("Tools/FeelFreeFlying/M0: シーンの中身を数える")]
        public static void DumpCurrentScene() => Dump();

        /// <summary>バッチモード用。計測シーンを開いてから数える。</summary>
        public static void DumpBenchmarkScene()
        {
            EditorSceneManager.OpenScene(BenchmarkScenePath, OpenSceneMode.Single);
            Dump();
        }

        private static void Dump()
        {
            var groups = Object.FindObjectsByType<PLATEAUCityObjectGroup>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var report = new StringBuilder();
            report.AppendLine($"scene                : {BenchmarkScenePath}");
            report.AppendLine($"PLATEAUCityObjectGroup: {groups.Length}");
            report.AppendLine($"MeshRenderer          : {renderers.Length}");

            // 粒度とLODの内訳。ここが設定どおりかを最初に見る。
            AppendBreakdown(report, "粒度", groups.Select(g => g.Granularity.ToString()));
            AppendBreakdown(report, "LOD", groups.Select(g => $"LOD{g.Lod}"));

            long triangles = 0;
            long vertices = 0;
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            Bounds? bounds = null;

            foreach (MeshRenderer renderer in renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null) materials.Add(material);
                }

                bounds = bounds == null
                    ? renderer.bounds
                    : Encapsulated(bounds.Value, renderer.bounds);

                var filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !meshes.Add(mesh)) continue;

                triangles += mesh.triangles.Length / 3;
                vertices += mesh.vertexCount;
            }

            report.AppendLine($"ユニークMesh          : {meshes.Count}");
            report.AppendLine($"三角形（重複除く）    : {triangles:N0}");
            report.AppendLine($"頂点（重複除く）      : {vertices:N0}");
            report.AppendLine($"ユニークMaterial      : {materials.Count}");

            if (bounds != null)
            {
                Vector3 size = bounds.Value.size;
                report.AppendLine(
                    $"広がり                : {size.x:F0} x {size.z:F0} m（高さ {size.y:F0} m）" +
                    $" 中心 ({bounds.Value.center.x:F0}, {bounds.Value.center.y:F0}, {bounds.Value.center.z:F0})");
            }

            AppendPerGroupDetail(report, groups);

            string text = report.ToString();
            Debug.Log("[M0Stats]\n" + text);

            Directory.CreateDirectory(OutputDir);
            File.WriteAllText(Path.Combine(OutputDir, "scene-stats.txt"), text, Encoding.UTF8);
            Debug.Log($"[M0Stats] 出力: {Path.Combine(OutputDir, "scene-stats.txt")}");
        }

        /// <summary>
        /// グループ単位の内訳。合計だけ見ていると「23個のオブジェクトに何棟入っているのか」
        /// （＝マージされているのか、そもそも建物が来ていないのか）が区別できない。
        /// </summary>
        private static void AppendPerGroupDetail(StringBuilder report, PLATEAUCityObjectGroup[] groups)
        {
            report.AppendLine();
            report.AppendLine("=== グループ別 ===");
            report.AppendLine("親 / 名前 / 粒度 / LOD / CityObject数 / 三角形 / 広がり(m)");

            int totalCityObjects = 0;

            foreach (PLATEAUCityObjectGroup group in groups.OrderBy(g => g.name))
            {
                int cityObjectCount = CountCityObjects(group);
                totalCityObjects += cityObjectCount;

                var filter = group.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                int triangles = mesh != null ? mesh.triangles.Length / 3 : 0;

                var renderer = group.GetComponent<MeshRenderer>();
                Vector3 size = renderer != null ? renderer.bounds.size : Vector3.zero;

                string parent = group.transform.parent != null ? group.transform.parent.name : "(root)";

                report.AppendLine(
                    $"  {parent} / {group.name} / {group.Granularity} / LOD{group.Lod} / " +
                    $"{cityObjectCount:N0} / {triangles:N0} / {size.x:F0}x{size.z:F0}x{size.y:F0}");
            }

            report.AppendLine($"CityObject合計        : {totalCityObjects:N0}");
        }

        /// <summary>グループが抱える地物の数。子まで辿る（マージされていると1グループに何千と入る）。</summary>
        private static int CountCityObjects(PLATEAUCityObjectGroup group)
        {
            CityObjectList list = group.CityObjects;
            if (list?.rootCityObjects == null) return 0;

            int count = 0;
            var stack = new Stack<CityObjectList.CityObject>(list.rootCityObjects);
            while (stack.Count > 0)
            {
                CityObjectList.CityObject current = stack.Pop();
                count++;
                if (current.Children == null) continue;
                foreach (CityObjectList.CityObject child in current.Children) stack.Push(child);
            }
            return count;
        }

        private static void AppendBreakdown(StringBuilder report, string title, IEnumerable<string> values)
        {
            var counts = new Dictionary<string, int>();
            foreach (string value in values)
            {
                counts.TryGetValue(value, out int count);
                counts[value] = count + 1;
            }

            if (counts.Count == 0)
            {
                report.AppendLine($"{title}                  : （該当なし）");
                return;
            }

            foreach (KeyValuePair<string, int> pair in counts.OrderByDescending(p => p.Value))
            {
                report.AppendLine(
                    $"{title} {pair.Key,-24}: {pair.Value.ToString("N0", CultureInfo.InvariantCulture)}");
            }
        }

        private static Bounds Encapsulated(Bounds a, Bounds b)
        {
            a.Encapsulate(b);
            return a;
        }
    }
}
