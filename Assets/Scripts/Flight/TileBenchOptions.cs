using System.Collections;
using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 計測の条件を実行時に外す（docs/m2-plan.md §5.1）。
    ///
    /// **ビルドし直さずに切り分けるため。** タイルに割った街は、M0で測った1シーンの街と
    /// 中身が違う——当たり判定を焼き込み、高さ別の外壁を当ててある。
    /// 平均フレームレートに差が出た時、どちらが効いているのかを同じビルドで確かめられないと、
    /// 条件が揃わないまま比較することになる。
    ///
    ///   FeelFreeFlying-M2Bench.exe -ffm2bench-nocolliders
    ///   FeelFreeFlying-M2Bench.exe -ffm2bench-plainmat
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TileBenchOptions : MonoBehaviour
    {
        [Tooltip("タイルが読み込まれるのを待つ秒数。計測の助走より短くする")]
        [SerializeField, Min(0f)] private float applyAfterSeconds = 3f;

        private IEnumerator Start()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool removeColliders = System.Array.IndexOf(args, "-ffm2bench-nocolliders") >= 0;
            bool plainMaterial = System.Array.IndexOf(args, "-ffm2bench-plainmat") >= 0;

            // **メモリの解放は破棄のたびに走る。** 引っかかりの出所がここかを確かめる
            if (System.Array.IndexOf(args, "-ffm2bench-nounload") >= 0)
            {
                var streamer = FindAnyObjectByType<TileStreamer>();
                if (streamer != null)
                {
                    streamer.ReleaseAssetsOnUnload = false;
                    Debug.Log("[BenchOptions] 破棄時のメモリ解放を切った");
                }
            }

            // **焼いたオクルージョンカリングを切る。** 効果を同じビルドで測るため
            if (System.Array.IndexOf(args, "-ffm2bench-noocclusion") >= 0)
            {
                foreach (Camera camera in Camera.allCameras) camera.useOcclusionCulling = false;
                Debug.Log("[BenchOptions] オクルージョンカリングを切った");
            }

            if (!removeColliders && !plainMaterial) yield break;

            yield return new WaitForSeconds(applyAfterSeconds);

            if (removeColliders) Debug.Log($"[BenchOptions] 当たり判定を外した: {RemoveColliders()} 件");
            if (plainMaterial) Debug.Log($"[BenchOptions] 無地に差し替えた: {ApplyPlainMaterial()} 個");
        }

        private static int RemoveColliders()
        {
            var colliders = FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (MeshCollider collider in colliders) Destroy(collider);
            return colliders.Length;
        }

        /// <summary>建物だけ無地にする。地形の航空写真は残す（判断材料が消える）。</summary>
        private static int ApplyPlainMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return 0;

            var plain = new Material(shader) { color = new Color(0.78f, 0.78f, 0.80f) };
            int count = 0;

            foreach (MeshRenderer renderer in FindObjectsByType<MeshRenderer>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!renderer.name.StartsWith("bldg_", System.StringComparison.Ordinal)) continue;

                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = plain;
                renderer.sharedMaterials = materials;
                count++;
            }

            return count;
        }
    }
}
