using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルの中身を測って**幾何の索引**を書き出す（docs/m3-plan.md §6「底面積をどこで計算するか」）。
    ///
    /// 属性表（<see cref="M2AttributeExport"/>）が持っているのは用途・高さ・階数だけで、
    /// **位置と底面積が無い**。M3の5ルールはどれも「周囲200〜300mと比べる」形をしているので、
    /// gmlIDに座標が付いていないと1つも計算できない。
    ///
    /// 座標はCityGMLからも計算できるが、**それはSDKの仕事**（位置表と同じ理由 →
    /// <see cref="FeelFreeFlying.Flight.TileCatalog"/>）。取り込んだ結果を測って書くほうが、
    /// 実際に画面に出ている街と必ず一致する。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M3BuildingIndex.Build `
    ///     -ffm2tiles-grid &lt;街全体のメッシュコード&gt;
    /// </summary>
    public static class M3BuildingIndex
    {
        public const string OutputDir = "Data/Plateau/attributes";
        public const string GeometryCsv = OutputDir + "/geometry.csv";
        public const string TerrainCsv = OutputDir + "/terrain.csv";

        /// <summary>地形を標本化する格子の一辺 (m)。**m3-plan.md §2.4・§2.5と同じ50m。**</summary>
        public const float TerrainCellSize = 50f;

        [MenuItem("Tools/FeelFreeFlying/M3: 建物の位置と底面積を測る")]
        public static void BuildFromMenu() => Run(exitWhenDone: false);

        public static void Build() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            try
            {
                int written = BuildInProcess();
                if (exitWhenDone) EditorApplication.Exit(written > 0 ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M3Index] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>他の工程から続けて呼ぶ入口。**ここでは終了しない。**</summary>
        public static int BuildInProcess()
        {
            string[] gridCodes = M2TilePipeline.GridCodesInUse
                .Where(code => File.Exists(M2TilePipeline.TileScenePath(code)))
                .ToArray();

            if (gridCodes.Length == 0)
            {
                Debug.LogError("[M3Index] タイルがありません。先に M2: 街をタイルに割って取り込む を実行してください。");
                return 0;
            }

            var buildings = new List<string>();
            var terrain = new Dictionary<(int, int), float>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int unreadable = 0;

            foreach (string gridCode in gridCodes)
            {
                string scenePath = M2TilePipeline.TileScenePath(gridCode);
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                int tileBuildings = 0;

                foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null) continue;

                    string name = filter.name;

                    if (name.StartsWith("dem_", StringComparison.Ordinal))
                    {
                        SampleTerrain(filter, mesh, terrain);
                        continue;
                    }

                    if (!name.StartsWith("bldg_", StringComparison.Ordinal)) continue;

                    // **同じ建物が2枚のタイルに入ることがある**（メッシュコードの境界をまたぐ建物）。
                    // 先に見たほうを採る——どちらも同じ形なので、重複だけ落とせばよい
                    if (!seen.Add(name)) continue;

                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null) continue;

                    Bounds bounds = renderer.bounds;
                    float footprint = TopViewArea(mesh, filter.transform, ref unreadable);

                    // 上から見た面積が取れない時だけ、外接箱で代用する。
                    // **箱は実際より広い**ので、代用したことが分かるように0以下では書かない
                    if (footprint <= 0f) footprint = bounds.size.x * bounds.size.z;

                    buildings.Add(string.Join(",",
                        name,
                        gridCode,
                        F(bounds.center.x), F(bounds.min.y), F(bounds.center.z),
                        F(bounds.size.x), F(bounds.size.y), F(bounds.size.z),
                        F(footprint)));

                    tileBuildings++;
                }

                Debug.Log($"[M3Index] {gridCode}: 建物 {tileBuildings} 棟 / 地形の格子 {terrain.Count} 個（累計）");
            }

            Directory.CreateDirectory(OutputDir);
            WriteGeometry(buildings);
            WriteTerrain(terrain);

            if (unreadable > 0)
            {
                Debug.LogWarning($"[M3Index] メッシュを読めず外接箱で代用した建物: {unreadable} 棟");
            }

            Debug.Log($"[M3Index] 完了: 建物 {buildings.Count} 棟 / 地形 {terrain.Count} 格子 → " +
                      $"{Path.GetFullPath(GeometryCsv)}");

            return buildings.Count;
        }

        /// <summary>
        /// **上から見た面積**を三角形から積む。外接箱では、L字の建物も中庭のある建物も
        /// 実際より広くなり、「大きな屋根」（R3）が形だけ大きい建物で埋まる。
        ///
        /// 上を向いた三角形（法線のyが正）をXZ平面に落とした面積を足すと、
        /// 屋根が高さの関数である限り——つまり張り出しが無い限り——真上から見た面積に一致する。
        /// 壁は真上から見ると潰れて0になるので、足しても引いても効かない。
        /// </summary>
        private static float TopViewArea(Mesh mesh, Transform transform, ref int unreadable)
        {
            if (!mesh.isReadable)
            {
                unreadable++;
                return 0f;
            }

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Matrix4x4 matrix = transform.localToWorldMatrix;

            double area = 0d;

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                Vector3 a = matrix.MultiplyPoint3x4(vertices[triangles[i]]);
                Vector3 b = matrix.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                Vector3 c = matrix.MultiplyPoint3x4(vertices[triangles[i + 2]]);

                // 外積のy成分。正なら上を向いている
                float upward = (b.z - a.z) * (c.x - a.x) - (b.x - a.x) * (c.z - a.z);
                if (upward > 0f) area += upward * 0.5d;
            }

            return (float)area;
        }

        /// <summary>
        /// 地形の高さを50m格子に落とす。**格子ごとに最大値**を採る——
        /// 見晴らしの丘（R5）は尾根の高さで決まるので、平均だと谷に引きずられて消える。
        /// </summary>
        private static void SampleTerrain(MeshFilter filter, Mesh mesh,
            IDictionary<(int, int), float> terrain)
        {
            if (!mesh.isReadable) return;

            Vector3[] vertices = mesh.vertices;
            Matrix4x4 matrix = filter.transform.localToWorldMatrix;

            foreach (Vector3 local in vertices)
            {
                Vector3 world = matrix.MultiplyPoint3x4(local);

                var cell = (Mathf.FloorToInt(world.x / TerrainCellSize),
                            Mathf.FloorToInt(world.z / TerrainCellSize));

                if (!terrain.TryGetValue(cell, out float height) || world.y > height)
                {
                    terrain[cell] = world.y;
                }
            }
        }

        private static void WriteGeometry(List<string> rows)
        {
            var csv = new StringBuilder();
            csv.AppendLine("gmlId,gridCode,x,baseY,z,sizeX,sizeY,sizeZ,footprint");

            // **書き出す順序を固定する。** 同じ入力から同じ結果が出ること（m3-plan.md §0）は、
            // 抽出だけでなく入力の側にも要る
            foreach (string row in rows.OrderBy(row => row, StringComparer.Ordinal)) csv.AppendLine(row);

            File.WriteAllText(GeometryCsv, csv.ToString(), new UTF8Encoding(false));
        }

        private static void WriteTerrain(Dictionary<(int, int), float> terrain)
        {
            var csv = new StringBuilder();
            csv.AppendLine("cellX,cellZ,height");

            foreach (KeyValuePair<(int, int), float> pair in terrain
                         .OrderBy(pair => pair.Key.Item1).ThenBy(pair => pair.Key.Item2))
            {
                csv.AppendLine($"{pair.Key.Item1},{pair.Key.Item2},{F(pair.Value)}");
            }

            File.WriteAllText(TerrainCsv, csv.ToString(), new UTF8Encoding(false));
        }

        private static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
