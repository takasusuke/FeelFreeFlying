using System;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// オクルージョンカリングを焼く（docs/m0-plan.md §5.1）。
    ///
    /// **効くのは街路の高さ。** 上空からは遮る物が少ないので、俯瞰の数字はほとんど変わらないはず。
    /// ビルの谷間を飛ぶ時、視界の外にある大量の建物を描かずに済むかどうかが本題で、
    /// M0で最後まで残った弱点（1% low）に直接効く可能性がある。
    ///
    /// **建物をstaticにする必要がある。** 焼く対象はOccluder/Occludee Staticの付いた
    /// オブジェクトだけなので、取り込んだ都市に印を付けてから焼く。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M3OcclusionCulling.Bake
    /// </summary>
    public static class M3OcclusionCulling
    {
        private const string ScenePath = "Assets/Scenes/M0Benchmark.unity";

        [MenuItem("Tools/FeelFreeFlying/M3: オクルージョンカリングを焼く")]
        public static void Bake()
        {
            try
            {
                Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                int marked = MarkCityStatic();
                Debug.Log($"[M3Occlusion] staticにした描画対象: {marked} 件");

                // 建物の谷間を通れる大きさに合わせる。**大きすぎると細い路地が塞がって
                // 見えるはずの物まで消える**（人が飛ぶので、目安は身体の大きさ）
                StaticOcclusionCulling.smallestOccluder = 5f;
                StaticOcclusionCulling.smallestHole = 1f;
                StaticOcclusionCulling.backfaceThreshold = 100f;

                var stopwatch = Stopwatch.StartNew();
                StaticOcclusionCulling.Compute();
                stopwatch.Stop();

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);

                Debug.Log(
                    $"[M3Occlusion] 完了: {stopwatch.Elapsed.TotalMinutes:F1} 分 / " +
                    $"データ {StaticOcclusionCulling.umbraDataSize / 1024f / 1024f:F1} MB");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M3Occlusion] 失敗: {exception}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>都市の描画対象をstaticにする。**機体（Flyer）は動くので対象外。**</summary>
        private static int MarkCityStatic()
        {
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.name.StartsWith("bldg_") ||
                                   renderer.name.StartsWith("dem_") ||
                                   renderer.name.StartsWith("group"))
                .ToList();

            foreach (MeshRenderer renderer in renderers)
            {
                GameObjectUtility.SetStaticEditorFlags(
                    renderer.gameObject,
                    StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic |
                    StaticEditorFlags.BatchingStatic);
            }

            return renderers.Count;
        }
    }
}
