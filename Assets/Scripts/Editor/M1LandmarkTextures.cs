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
        private const string CountArg = "-fflandmarks";
        private const int DefaultLandmarkCount = 40;

        /// <summary>この高さ以下は、上位に入っても対象にしない (m)。</summary>
        private const float MinimumLandmarkHeight = 40f;

        [MenuItem("Tools/FeelFreeFlying/M1: ランドマークだけ実写テクスチャにする")]
        public static void Apply()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // **建物だけを対象にする。** 地形（dem_*）まで差し替えると、地面に貼られている
            // 航空写真（地理院タイル）が無地になり、公園も道路も見えなくなる（一度やった）
            var buildings = Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.GetComponent<MeshFilter>() != null)
                .Where(renderer => renderer.name.StartsWith("bldg_"))
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

            var landmarkSet = new HashSet<MeshRenderer>(landmarks);
            var materialCache = new Dictionary<string, Material>();
            var tierCounts = new Dictionary<string, int>();
            int replaced = 0;

            foreach (MeshRenderer renderer in buildings)
            {
                if (landmarkSet.Contains(renderer)) continue;

                Material generic = MaterialForHeight(renderer.bounds.size.y, materialCache);

                var materials = new Material[renderer.sharedMaterials.Length];
                for (int i = 0; i < materials.Length; i++) materials[i] = generic;
                renderer.sharedMaterials = materials;

                tierCounts.TryGetValue(generic.name, out int seen);
                tierCounts[generic.name] = seen + 1;
                replaced++;
            }

            int discarded = DiscardUnreferencedTextures(landmarks);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log(
                $"[M1Landmark] 実写のまま残した建物: {landmarks.Count} 棟 / " +
                $"様式化に差し替え: {replaced} 棟 / 捨てたテクスチャ: {discarded} 枚");

            foreach (KeyValuePair<string, int> tier in tierCounts.OrderByDescending(pair => pair.Value))
            {
                Debug.Log($"[M1Landmark]   {tier.Key}: {tier.Value} 棟");
            }

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
        /// <summary>タイル単位の変換からも同じ処理を呼ぶ（M2）。</summary>
        public static int DiscardUnreferencedTextures(IEnumerable<MeshRenderer> landmarks)
        {
            var keep = new HashSet<Texture>();

            // 建物以外（地形の航空写真など）が持つテクスチャは捨てない
            IEnumerable<MeshRenderer> keepAll = Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => !renderer.name.StartsWith("bldg_"))
                .Concat(landmarks);

            foreach (MeshRenderer landmark in keepAll)
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
        /// ランドマーク以外に貼るマテリアルを**高さで使い分ける**。
        ///
        /// 全部同じ外壁だと、街が一様に見えて「どのあたりを飛んでいるか」が分からない。
        /// 低層の住宅地と中層のオフィス街と高層ビル群が描き分けられるだけで、
        /// 上空から見た時の情報量が変わる。
        ///
        /// 使うのはSDK同梱の汎用テクスチャで、**パッケージ側のアセットなのでビルド容量は増えない**
        /// （実写テクスチャはシーンに埋め込まれるので増える）。
        /// 様式化そのものはM5の課題。ここでは「実写ではない」「浮かない」「一様でない」で足りる。
        /// </summary>
        private static readonly (float MaxHeight, string MaterialName)[] HeightTiers =
        {
            (12f, "PlateauGenericHouse"),        // 低層（住宅）
            (30f, "PlateauDefaultBuildingC"),    // 中層
            (70f, "PlateauDefaultBuildingB"),    // 中高層（オフィス）
            (float.MaxValue, "PlateauDefaultBuildingA"), // 高層
        };

        private const string MaterialFolder = "Packages/com.synesthesias.plateau-unity-sdk/Resources/PlateauSdkDefaultMaterials";

        /// <summary>高さから外壁を選ぶ。**タイル単位の変換からも同じ基準で呼ぶ**（M2）。</summary>
        public static Material MaterialForHeight(float height, Dictionary<string, Material> cache)
        {
            string name = HeightTiers.First(tier => height < tier.MaxHeight).MaterialName;

            if (cache.TryGetValue(name, out Material cached)) return cached;

            var material = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{name}.mat");
            if (material == null)
            {
                Debug.LogWarning($"[M1Landmark] {name} が見つかりません。無地で代用します。");
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                {
                    color = new Color(0.78f, 0.78f, 0.80f),
                };
            }

            cache[name] = material;
            return material;
        }
    }
}
