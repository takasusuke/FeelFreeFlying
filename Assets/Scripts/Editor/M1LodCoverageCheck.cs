using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// **LOD2に全部の建物が入っているか**を確かめる（docs/m1-plan.md §6.3）。
    ///
    /// LOD2だけを指定して取り込むと、LOD2を持たない建物は**黙って消える**。
    /// 建物数だけ見ても、LOD2で建物が分割・結合される可能性があるため断定できない。
    /// 2つのシーンの建物名（`bldg_&lt;uuid&gt;`）を突き合わせて、どちらにしか無いものを数える。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1LodCoverageCheck.Compare
    /// </summary>
    public static class M1LodCoverageCheck
    {
        private const string Lod1Scene = "Assets/Scenes/M0Benchmark_PerPrimary.unity";
        private const string Lod2Scene = "Assets/Scenes/M0Benchmark.unity";

        [MenuItem("Tools/FeelFreeFlying/M1: LODごとの建物の取りこぼしを調べる")]
        public static void Compare()
        {
            HashSet<string> lod1 = CollectBuildingNames(Lod1Scene);
            HashSet<string> lod2 = CollectBuildingNames(Lod2Scene);

            if (lod1.Count == 0 || lod2.Count == 0)
            {
                Debug.LogError("[M1Lod] 建物が見つかりません。比較するシーンを確認してください。");
                EditorApplication.Exit(1);
                return;
            }

            var missingInLod2 = lod1.Where(name => !lod2.Contains(name)).ToList();
            var onlyInLod2 = lod2.Where(name => !lod1.Contains(name)).ToList();

            Debug.Log(
                $"[M1Lod] LOD1: {lod1.Count} 棟 / LOD2: {lod2.Count} 棟\n" +
                $"[M1Lod] **LOD2で消える建物: {missingInLod2.Count} 棟**（LOD1にあってLOD2に無い）\n" +
                $"[M1Lod] LOD2にだけある建物: {onlyInLod2.Count} 棟");

            foreach (string name in missingInLod2.Take(5))
            {
                Debug.Log($"[M1Lod]   消える例: {name}");
            }
        }

        private static HashSet<string> CollectBuildingNames(string scenePath)
        {
            var names = new HashSet<string>();
            if (!System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning($"[M1Lod] シーンがありません: {scenePath}");
                return names;
            }

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            foreach (MeshRenderer renderer in Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer.name.StartsWith("bldg_")) names.Add(renderer.name);
            }

            return names;
        }
    }
}
