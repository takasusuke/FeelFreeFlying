using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// タイルの建物に高さ別の外壁を当てる（docs/m2-plan.md §2 の「4. マテリアル」）。
    ///
    /// **M1で決めた見た目をタイルに割った後も保つための工程。**
    /// 高さで4段階に描き分けると、上空から「どのあたりを飛んでいるか」が分かる
    /// （→ `m1-plan.md` §6）。当てないと街が一様な灰色になり、M1の判断が再現できない。
    ///
    /// 基準は<see cref="M1LandmarkTextures.MaterialForHeight"/>と共有する。
    /// **タイルごとに別の基準で塗ると、タイルの継ぎ目で街の色が変わる。**
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2TileMaterials.Apply
    /// </summary>
    public static class M2TileMaterials
    {
        [MenuItem("Tools/FeelFreeFlying/M2: タイルに外壁を当てる")]
        public static void ApplyFromMenu() => Run(exitWhenDone: false);

        public static void Apply() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            try
            {
                string[] scenes = Directory.Exists(M2TilePipeline.TileDir)
                    ? Directory.GetFiles(M2TilePipeline.TileDir, "Tile_*.unity")
                        .Select(path => path.Replace('\\', '/'))
                        .OrderBy(path => path)
                        .ToArray()
                    : new string[0];

                if (scenes.Length == 0)
                {
                    Debug.LogError("[M2Material] タイルがありません。");
                    if (exitWhenDone) EditorApplication.Exit(1);
                    return;
                }

                var cache = new Dictionary<string, Material>();
                var tierTotals = new Dictionary<string, int>();

                foreach (string scenePath in scenes)
                {
                    Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    int painted = Paint(cache, tierTotals);

                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene, scenePath);

                    Debug.Log($"[M2Material] {Path.GetFileNameWithoutExtension(scenePath)}: {painted} 棟");
                }

                foreach (KeyValuePair<string, int> tier in tierTotals.OrderByDescending(pair => pair.Value))
                {
                    Debug.Log($"[M2Material]   {tier.Key}: {tier.Value} 棟");
                }

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Material] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 建物だけ塗る。**地形（dem_*）まで塗ると地面の航空写真が無地になる**
        /// （M1で一度やった → `m1-plan.md` §6.3）。
        /// </summary>
        public static int Paint(Dictionary<string, Material> cache, Dictionary<string, int> tierTotals)
        {
            var buildings = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.name.StartsWith("bldg_", StringComparison.Ordinal))
                .ToList();

            foreach (MeshRenderer renderer in buildings)
            {
                Material generic = M1LandmarkTextures.MaterialForHeight(renderer.bounds.size.y, cache);

                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = generic;
                renderer.sharedMaterials = materials;

                tierTotals.TryGetValue(generic.name, out int seen);
                tierTotals[generic.name] = seen + 1;
            }

            return buildings.Count;
        }
    }
}
