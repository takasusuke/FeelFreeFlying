using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// ランドマークだけ実写テクスチャを残し、他の建物は様式化マテリアルに差し替える
    /// （docs/m1-plan.md §6.3）。
    ///
    /// **どれをランドマークにするかは手で選ばない**（`CLAUDE.md` 不変条件7）。高さで上位N棟を採る。
    /// 手で選ぶと都市を追加するたびに人が選び直すことになり、パイプラインが止まる。
    ///
    /// 差し替えた建物はテクスチャへの参照を失うので、**そのテクスチャはビルドにも入らない**。
    /// これが「ランドマークだけ実写」で容量を抑えられる理由。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1LandmarkTextures.Apply -fflandmarks 15
    /// </summary>
    public static class M1LandmarkTextures
    {
        private const string ScenePath = "Assets/Scenes/M0Benchmark.unity";
        private const string StylizedMaterialPath = "Assets/Settings/Rendering/StylizedBuilding.mat";
        private const string CountArg = "-fflandmarks";
        private const int DefaultLandmarkCount = 15;

        /// <summary>この高さ以下は、上位に入っても対象にしない (m)。</summary>
        private const float MinimumLandmarkHeight = 60f;

        [MenuItem("Tools/FeelFreeFlying/M1: ランドマークだけ実写テクスチャにする")]
        public static void Apply()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var buildings = Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.GetComponent<MeshFilter>() != null)
                .ToList();

            if (buildings.Count < 100)
            {
                Debug.LogError(
                    $"[M1Landmark] 建物が{buildings.Count}個しかありません。" +
                    "**建物単位（主要地物単位）で取り込んでください。** " +
                    "地域単位はメッシュが結合済みで、建物ごとにマテリアルを分けられません。");
                EditorApplication.Exit(1);
                return;
            }

            int count = ResolveCount();

            // 高さで並べる。ランドマークは「高い建物」だけではないが、
            // **計算で選べる基準**であることを優先する（手で選ばない）
            List<MeshRenderer> landmarks = buildings
                .Where(renderer => renderer.bounds.size.y >= MinimumLandmarkHeight)
                .OrderByDescending(renderer => renderer.bounds.size.y)
                .Take(count)
                .ToList();

            Material stylized = LoadOrCreateStylizedMaterial();
            var landmarkSet = new HashSet<MeshRenderer>(landmarks);
            int replaced = 0;

            foreach (MeshRenderer renderer in buildings)
            {
                if (landmarkSet.Contains(renderer)) continue;

                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = stylized;
                renderer.sharedMaterials = materials;
                replaced++;
            }

            int discarded = DiscardUnreferencedTextures(landmarks);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log(
                $"[M1Landmark] 実写のまま残した建物: {landmarks.Count} 棟 / " +
                $"様式化に差し替え: {replaced} 棟 / 捨てたテクスチャ: {discarded} 枚");

            foreach (MeshRenderer landmark in landmarks)
            {
                Debug.Log($"[M1Landmark]   高さ {landmark.bounds.size.y:F0} m  {landmark.name}");
            }
        }

        /// <summary>
        /// ランドマーク以外のテクスチャをシーンから捨てる。
        ///
        /// **マテリアルを差し替えて参照を切るだけでは足りない。** PLATEAUのテクスチャは
        /// アセットではなく**シーンに埋め込まれた**オブジェクトなので、参照されなくなっても
        /// シーンに残り、そのままビルドに入る（実測でビルドが451MB膨らんだ）。
        /// </summary>
        private static int DiscardUnreferencedTextures(IEnumerable<MeshRenderer> landmarks)
        {
            var keep = new HashSet<Texture>();
            foreach (MeshRenderer landmark in landmarks)
            {
                foreach (Material material in landmark.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (string property in material.GetTexturePropertyNames())
                    {
                        Texture texture = material.GetTexture(property);
                        if (texture != null) keep.Add(texture);
                    }
                }
            }

            int discarded = 0;
            foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                // アセットとして存在するもの（SDK同梱・自作）は消さない。消すのは
                // シーンに埋め込まれた取り込み由来のものだけ
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture))) continue;
                if (keep.Contains(texture)) continue;
                if ((texture.hideFlags & HideFlags.DontSave) != 0) continue;

                Object.DestroyImmediate(texture, true);
                discarded++;
            }

            return discarded;
        }

        private static int ResolveCount()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == CountArg && int.TryParse(args[i + 1], out int parsed)) return parsed;
            }
            return DefaultLandmarkCount;
        }

        /// <summary>
        /// 差し替え先の仮マテリアル。**様式化そのものはM5の課題**なので、
        /// ここでは「実写ではない」ことだけ満たす無地にしておく。
        /// </summary>
        private static Material LoadOrCreateStylizedMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(StylizedMaterialPath);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader)
            {
                color = new Color(0.78f, 0.78f, 0.80f),
            };
            material.SetFloat("_Smoothness", 0.15f);

            AssetDatabase.CreateAsset(material, StylizedMaterialPath);
            AssetDatabase.SaveAssets();
            return material;
        }
    }
}
